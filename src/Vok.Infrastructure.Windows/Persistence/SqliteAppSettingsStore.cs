using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Vok.Core.Appearance;
using Vok.Core.Platform;

namespace Vok.Infrastructure.Windows.Persistence;

public sealed class SqliteAppSettingsStore : IAppSettingsStore, IDisposable
{
    private const string ThemeModeKey = "appearance.theme-mode";
    private const string HotkeyGestureKey = "input.global-hotkey";
    private readonly SqliteStorageExecutor _executor;
    private readonly bool _ownsExecutor;

    public SqliteAppSettingsStore(string databasePath)
        : this(new SqliteStorageExecutor(databasePath), ownsExecutor: true)
    {
    }

    public SqliteAppSettingsStore(SqliteStorageExecutor executor)
        : this(executor, ownsExecutor: false)
    {
    }

    private SqliteAppSettingsStore(
        SqliteStorageExecutor executor,
        bool ownsExecutor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _ownsExecutor = ownsExecutor;
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

    public Task SaveHotkeyGestureAsync(
        GlobalHotkeyGesture gesture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        return _executor.ExecuteAsync(
            connection =>
            {
                SaveSetting(
                    connection,
                    HotkeyGestureKey,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{(uint)gesture.Modifiers}:{gesture.VirtualKey}"));
                return true;
            },
            cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsExecutor)
        {
            _executor.Dispose();
        }
    }

    private static AppSettings Load(SqliteConnection connection)
    {
        var themeValue = LoadSetting(connection, ThemeModeKey);
        var themeMode = Enum.TryParse<AppThemeMode>(
                            themeValue,
                            ignoreCase: true,
                            out var parsedThemeMode) &&
                        Enum.IsDefined(parsedThemeMode)
            ? parsedThemeMode
            : AppThemeMode.System;

        var hotkeyGesture = TryParseHotkeyGesture(
            LoadSetting(connection, HotkeyGestureKey));
        return new AppSettings(
            themeMode,
            hotkeyGesture ?? GlobalHotkeyGesture.Default);
    }

    private static void SaveThemeMode(
        SqliteConnection connection,
        AppThemeMode themeMode) =>
        SaveSetting(connection, ThemeModeKey, themeMode.ToString());

    private static string? LoadSetting(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value
            FROM app_settings
            WHERE key = $key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void SaveSetting(
        SqliteConnection connection,
        string key,
        string value)
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
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue(
            "$updatedUtc",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static GlobalHotkeyGesture? TryParseHotkeyGesture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1 ||
            !uint.TryParse(
                value.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var modifiers) ||
            !uint.TryParse(
                value.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var virtualKey) ||
            !GlobalHotkeyGesture.TryCreate(
                (GlobalHotkeyModifiers)modifiers,
                virtualKey,
                out var gesture))
        {
            return null;
        }

        return gesture;
    }
}
