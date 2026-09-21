using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Vok.Core.Stage;
using Vok.Infrastructure.Windows.Persistence;

namespace Vok.StageScaleProbe;

internal static class StageScaleData
{
    internal const int SeedBatchSize = 10_000;
    private const string SourceContents = "Vok isolated Stage scale probe source.\r\n";

    internal static async Task<PrepareProbeReport> PrepareAsync(
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(options.Root);
        await WriteNewTextAsync(
                options.SourcePath,
                SourceContents,
                cancellationToken)
            .ConfigureAwait(false);
        var sourceHash = ComputeFileSha256(options.SourcePath);
        var canonicalKey = Path.GetFullPath(options.SourcePath).ToUpperInvariant();

        var stopwatch = Stopwatch.StartNew();
        using (var repository = new SqliteStageRepository(
                   options.DatabasePath,
                   new StageOptions(options.ItemCount)))
        {
            for (var offset = 0; offset < options.ItemCount; offset += SeedBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(SeedBatchSize, options.ItemCount - offset);
                var targets = new StageTargetDescriptor[count];
                for (var index = 0; index < count; index++)
                {
                    var occurrence = checked(offset + index + 1);
                    targets[index] = new StageTargetDescriptor(
                        StageItemKind.File,
                        options.SourcePath,
                        canonicalKey,
                        $"scale-{occurrence:D6}.txt");
                }

                var result = await repository
                    .AddBatchAsync(targets, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status is not StageAddBatchStatus.Applied ||
                    result.AddedCount != count || result.CountAfter != offset + count)
                {
                    throw new InvalidOperationException(
                        $"Seed batch {offset / SeedBatchSize + 1} failed: " +
                        $"status={result.Status}; added={result.AddedCount}; " +
                        $"countAfter={result.CountAfter}.");
                }

                Console.WriteLine($"seed {result.CountAfter}/{options.ItemCount}");
            }
        }

        stopwatch.Stop();
        var database = AuditDatabase(options.DatabasePath);
        if (database.StageCount != options.ItemCount || database.QuickCheck != "ok")
        {
            throw new InvalidDataException(
                $"Prepared database audit failed: count={database.StageCount}; " +
                $"quick_check={database.QuickCheck}.");
        }

        var marker = new ProbeMarker(
            MarkerVersion: 1,
            CreatedUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            SourceRevision: options.SourceRevision,
            ItemCount: options.ItemCount,
            DatabaseFileName: Path.GetFileName(options.DatabasePath),
            SourceFileName: Path.GetFileName(options.SourcePath),
            SourceSha256: sourceHash,
            StageDigest: database.StageDigest);
        ProbeReportWriter.WriteNew(options.MarkerPath, marker);

        var (executablePath, executableHash) = GetExecutableIdentity();
        var report = new PrepareProbeReport(
            ReportVersion: 1,
            Status: "Succeeded",
            Mode: "Prepare",
            StartedUtc: started.ToString("O", CultureInfo.InvariantCulture),
            CompletedUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            SourceRevision: options.SourceRevision,
            ExecutablePath: executablePath,
            ExecutableSha256: executableHash,
            Root: options.Root,
            RequestedItemCount: options.ItemCount,
            SeedBatchSize: SeedBatchSize,
            SeedElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            SourceSha256: sourceHash,
            Database: database);
        ProbeReportWriter.WriteNew(options.ReportPath, report);
        return report;
    }

    internal static ProbeMarker ValidatePreparedFixture(ProbeOptions options)
    {
        if (!File.Exists(options.MarkerPath) ||
            !File.Exists(options.DatabasePath) ||
            !File.Exists(options.SourcePath))
        {
            throw new InvalidDataException(
                "The scale fixture is incomplete; marker, database, or source is missing.");
        }

        var marker = ProbeReportWriter.Read<ProbeMarker>(options.MarkerPath);
        if (marker.MarkerVersion != 1 || marker.ItemCount != options.ItemCount ||
            !string.Equals(
                marker.SourceRevision,
                options.SourceRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The prepared fixture does not match the requested revision or item count.");
        }

        var sourceHash = ComputeFileSha256(options.SourcePath);
        if (!string.Equals(sourceHash, marker.SourceSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The probe-owned source file changed after prepare.");
        }

        var before = AuditDatabase(options.DatabasePath, marker.StageDigest);
        if (before.UserVersion != 8 || before.StageCount != options.ItemCount ||
            before.QuickCheck != "ok")
        {
            throw new InvalidDataException(
                $"The prepared database is invalid: version={before.UserVersion}; " +
                $"count={before.StageCount}; quick_check={before.QuickCheck}.");
        }

        return marker;
    }

    internal static DatabaseAudit AuditDatabase(
        string databasePath,
        string? trustedStageDigest = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        var userVersion = ReadInt64(connection, "PRAGMA user_version;");
        var stageCount = ReadInt64(connection, "SELECT COUNT(*) FROM stage_items;");
        var quickCheck = ReadString(connection, "PRAGMA quick_check;");
        var recomputed = trustedStageDigest is null;
        var digest = trustedStageDigest ?? ComputeStageDigest(connection);
        return new DatabaseAudit(
            userVersion,
            stageCount,
            quickCheck,
            digest,
            recomputed,
            new FileInfo(databasePath).Length,
            File.Exists($"{databasePath}-wal")
                ? new FileInfo($"{databasePath}-wal").Length
                : 0);
    }

    internal static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static (string Path, string Sha256) GetExecutableIdentity()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("The probe executable path is unavailable.");
        }

        return (executablePath, ComputeFileSha256(executablePath));
    }

    private static async Task WriteNewTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        var bytes = Encoding.UTF8.GetBytes(contents);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long ReadInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ReadString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ??
            string.Empty;
    }

    private static string ComputeStageDigest(SqliteConnection connection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, sort_order, is_pinned, revision,
                   created_utc, updated_utc
            FROM stage_items
            ORDER BY sort_order DESC, id DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = Convert.ToString(
                    reader.GetValue(index),
                    CultureInfo.InvariantCulture) ?? string.Empty;
                var bytes = Encoding.UTF8.GetBytes(value);
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
