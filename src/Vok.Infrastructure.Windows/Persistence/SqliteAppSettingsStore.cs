using System.Globalization;
using Microsoft.Data.Sqlite;
using Vok.Core.Appearance;

namespace Vok.Infrastructure.Windows.Persistence;

public sealed class SqliteAppSettingsStore : IAppSettingsStore, IDisposable
{
    private const string ThemeModeKey = "appearance.theme-mode";
    private readonly SqliteStorageExecutor _executor;

    public SqliteAppSettingsStore(string databasePath)
    {
        _executor = new SqliteStorageExecutor(databasePath);
    }

    public static SqliteAppSettingsStore CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        return new SqliteAppSettingsStore(
            Path.Combine(localAppData, "Vok", "vok.db"));
    }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        _executor.ExecuteAsync(Load, cancellationToken);

    public Task SaveThemeModeAsync(
        AppThemeMode themeMode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(themeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(themeMode), themeMode, null);
        }

        return _executor.ExecuteAsync(
            connection =>
            {
                SaveThemeMode(connection, themeMode);
                return true;
            },
            cancellationToken);
    }

    public void Dispose() => _executor.Dispose();

    private static AppSettings Load(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value
            FROM app_settings
            WHERE key = $key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", ThemeModeKey);
        var value = command.ExecuteScalar() as string;
        return Enum.TryParse<AppThemeMode>(value, ignoreCase: true, out var themeMode) &&
               Enum.IsDefined(themeMode)
            ? new AppSettings(themeMode)
            : AppSettings.Default;
    }

    private static void SaveThemeMode(
        SqliteConnection connection,
        AppThemeMode themeMode)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_settings (key, value, updated_utc)
            VALUES ($key, $value, $updatedUtc)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", ThemeModeKey);
        command.Parameters.AddWithValue("$value", themeMode.ToString());
        command.Parameters.AddWithValue(
            "$updatedUtc",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
