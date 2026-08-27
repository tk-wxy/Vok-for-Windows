using System.IO;
using Vok.Core.Appearance;
using Vok.Core.Platform;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Persistence;

public sealed class SqliteAppSettingsStoreTests
{
    [Fact]
    public async Task ThemeDefaultsToSystemAndSurvivesStoreRestart()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "settings.db");
        try
        {
            using (var store = new SqliteAppSettingsStore(databasePath))
            {
                Assert.Equal(
                    AppThemeMode.System,
                    (await store.LoadAsync(TestContext.Current.CancellationToken)).ThemeMode);
                Assert.Equal(
                    GlobalHotkeyGesture.Default,
                    (await store.LoadAsync(TestContext.Current.CancellationToken)).HotkeyGesture);
                await store.SaveThemeModeAsync(
                    AppThemeMode.Light,
                    TestContext.Current.CancellationToken);
                Assert.Equal(
                    AppThemeMode.Light,
                    (await store.LoadAsync(TestContext.Current.CancellationToken)).ThemeMode);
            }

            using var reopenedStore = new SqliteAppSettingsStore(databasePath);
            Assert.Equal(
                AppThemeMode.Light,
                (await reopenedStore.LoadAsync(TestContext.Current.CancellationToken)).ThemeMode);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task HotkeyGestureSurvivesStoreRestart()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "hotkey.db");
        var gesture = new GlobalHotkeyGesture(
            GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift,
            0x4B);
        try
        {
            using (var store = new SqliteAppSettingsStore(databasePath))
            {
                await store.SaveHotkeyGestureAsync(
                    gesture,
                    TestContext.Current.CancellationToken);
            }

            using var reopenedStore = new SqliteAppSettingsStore(databasePath);
            Assert.Equal(
                gesture,
                (await reopenedStore.LoadAsync(
                    TestContext.Current.CancellationToken)).HotkeyGesture);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task StorageOperationsRunOffTheCallingThread()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var executor = new SqliteStorageExecutor(
                Path.Combine(directory, "executor.db"));
            var callerThread = Environment.CurrentManagedThreadId;

            var storageThread = await executor.ExecuteAsync(
                _ => Environment.CurrentManagedThreadId,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(callerThread, storageThread);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task InvalidThemeValueFallsBackToSystem()
    {
        var directory = CreateTestDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "invalid.db");
            using (var executor = new SqliteStorageExecutor(databasePath))
            {
                await executor.ExecuteAsync(
                    connection =>
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = """
                            INSERT INTO app_settings (key, value, updated_utc)
                            VALUES ('appearance.theme-mode', 'Unexpected', '2026-08-27T00:00:00Z');
                            """;
                        return command.ExecuteNonQuery();
                    },
                    TestContext.Current.CancellationToken);
            }

            using var store = new SqliteAppSettingsStore(databasePath);
            Assert.Equal(
                AppThemeMode.System,
                (await store.LoadAsync(TestContext.Current.CancellationToken)).ThemeMode);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task InvalidHotkeyValueFallsBackWithoutDiscardingTheme()
    {
        var directory = CreateTestDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "invalid-hotkey.db");
            using (var executor = new SqliteStorageExecutor(databasePath))
            {
                await executor.ExecuteAsync(
                    connection =>
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = """
                            INSERT INTO app_settings (key, value, updated_utc)
                            VALUES
                                ('appearance.theme-mode', 'Dark', '2026-08-27T00:00:00Z'),
                                ('input.global-hotkey', '0:123', '2026-08-27T00:00:00Z');
                            """;
                        return command.ExecuteNonQuery();
                    },
                    TestContext.Current.CancellationToken);
            }

            using var store = new SqliteAppSettingsStore(databasePath);
            var settings = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AppThemeMode.Dark, settings.ThemeMode);
            Assert.Equal(GlobalHotkeyGesture.Default, settings.HotkeyGesture);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task CancellationAfterExecutionStartsDoesNotHideCommittedResult()
    {
        var directory = CreateTestDirectory();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        SqliteStorageExecutor? executor = null;
        try
        {
            executor = new SqliteStorageExecutor(
                Path.Combine(directory, "cancellation.db"));
            var operation = executor.ExecuteAsync(
                _ =>
                {
                    started.Set();
                    release.Wait(TestContext.Current.CancellationToken);
                    return 42;
                },
                cancellation.Token);
            started.Wait(TestContext.Current.CancellationToken);

            cancellation.Cancel();
            Assert.False(operation.IsCompleted);
            release.Set();

            Assert.Equal(42, await operation);
        }
        finally
        {
            release.Set();
            executor?.Dispose();
            DeleteTestDirectory(directory);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Vok.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
