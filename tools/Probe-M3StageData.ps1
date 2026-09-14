[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'Audit',
        'Backup',
        'ReplaySchema8',
        'ReplaySchema7',
        'ReplaySchema6',
        'ReplaySchema5')]
    [string] $Mode,

    [Parameter(Mandatory)] [string] $DatabasePath,
    [Parameter(Mandatory)] [string] $AppAssemblyDirectory,
    [Parameter(Mandatory)] [string] $OutputPath,
    [string] $AssetRoot,
    [string] $BackupPath,
    [string] $AllowedMutationRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Report = [ordered]@{
    ReportVersion = 1
    Mode = $Mode
    Status = 'Started'
    Stage = 'Initialization'
    StartedUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$script:NativeSearchPathSet = $false

function Resolve-AbsolutePath {
    param([Parameter(Mandatory)] [string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A required path was empty.'
    }

    [IO.Path]::GetFullPath($Path)
}

function Test-PathWithinRoot {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Root
    )

    $absolutePath = Resolve-AbsolutePath $Path
    $absoluteRoot = (Resolve-AbsolutePath $Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $absoluteRoot + [IO.Path]::DirectorySeparatorChar
    $absolutePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Save-Report {
    $absoluteOutput = Resolve-AbsolutePath $OutputPath
    $parent = Split-Path -Parent $absoluteOutput
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $temporary = Join-Path $parent (
        ([IO.Path]::GetFileName($absoluteOutput)) + '.tmp-' + [Guid]::NewGuid().ToString('N'))
    try {
        $script:Report | ConvertTo-Json -Depth 14 |
            Set-Content -LiteralPath $temporary -Encoding utf8
        Move-Item -LiteralPath $temporary -Destination $absoluteOutput -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Set-ReportStage {
    param([Parameter(Mandatory)] [string] $Stage)

    $script:Report.Stage = $Stage
    Save-Report
}

function Import-VokSqliteAssemblies {
    $assemblyRoot = Resolve-AbsolutePath $AppAssemblyDirectory
    if (-not (Test-Path -LiteralPath $assemblyRoot -PathType Container)) {
        throw "App assembly directory does not exist: $assemblyRoot"
    }

    $requiredManaged = @(
        'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll',
        'SQLitePCLRaw.batteries_v2.dll',
        'Microsoft.Data.Sqlite.dll'
    )
    foreach ($name in $requiredManaged) {
        $path = Join-Path $assemblyRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required SQLite assembly is missing: $path"
        }
    }

    $nativeCandidates = @(
        (Join-Path $assemblyRoot 'e_sqlite3.dll'),
        (Join-Path $assemblyRoot 'runtimes\win-x64\native\e_sqlite3.dll')
    )
    $nativePath = $nativeCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($nativePath)) {
        throw "The win-x64 e_sqlite3 native library is missing under: $assemblyRoot"
    }

    if (-not ('VokM3NativeLibraryPath' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;

public static class VokM3NativeLibraryPath
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetDllDirectory(string path);
}
'@
    }

    $nativeDirectory = Split-Path -Parent $nativePath
    if (-not [VokM3NativeLibraryPath]::SetDllDirectory($nativeDirectory)) {
        throw "Unable to set the SQLite native search directory: $nativeDirectory"
    }
    $script:NativeSearchPathSet = $true

    [void][Reflection.Assembly]::LoadFrom((Join-Path $assemblyRoot 'SQLitePCLRaw.core.dll'))
    [void][Reflection.Assembly]::LoadFrom((
        Join-Path $assemblyRoot 'SQLitePCLRaw.provider.e_sqlite3.dll'))
    [void][Reflection.Assembly]::LoadFrom((
        Join-Path $assemblyRoot 'SQLitePCLRaw.batteries_v2.dll'))
    [SQLitePCL.Batteries_V2]::Init()
    [void][Reflection.Assembly]::LoadFrom((Join-Path $assemblyRoot 'Microsoft.Data.Sqlite.dll'))

    if ($Mode -in @(
            'ReplaySchema8',
            'ReplaySchema7',
            'ReplaySchema6',
            'ReplaySchema5')) {
        foreach ($name in @('Vok.Core.dll', 'Vok.Infrastructure.Windows.dll')) {
            $path = Join-Path $assemblyRoot $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Required Vok assembly is missing: $path"
            }
            [void][Reflection.Assembly]::LoadFrom($path)
        }
    }
}

function New-SqliteConnection {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [bool] $ReadOnly
    )

    $builder = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
    $builder.DataSource = Resolve-AbsolutePath $Path
    $builder.Mode = if ($ReadOnly) {
        [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadOnly
    }
    else {
        [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadWriteCreate
    }
    $builder.Cache = [Microsoft.Data.Sqlite.SqliteCacheMode]::Private
    $builder.Pooling = $false
    [Microsoft.Data.Sqlite.SqliteConnection]::new($builder.ToString())
}

function Invoke-Scalar {
    param(
        [Parameter(Mandatory)] $Connection,
        [Parameter(Mandatory)] [string] $Sql
    )

    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = $Sql
        $command.ExecuteScalar()
    }
    finally {
        $command.Dispose()
    }
}

function Get-QueryDigest {
    param(
        [Parameter(Mandatory)] $Connection,
        [Parameter(Mandatory)] [string] $Sql
    )

    $command = $Connection.CreateCommand()
    $hasher = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    $encoding = [Text.Encoding]::UTF8
    try {
        $command.CommandText = $Sql
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                for ($ordinal = 0; $ordinal -lt $reader.FieldCount; $ordinal++) {
                    if ($reader.IsDBNull($ordinal)) {
                        $token = 'N;'
                    }
                    else {
                        $value = $reader.GetValue($ordinal)
                        if ($value -is [byte[]]) {
                            $text = [Convert]::ToHexString($value)
                            $typeName = 'B'
                        }
                        else {
                            $text = [Convert]::ToString(
                                $value,
                                [Globalization.CultureInfo]::InvariantCulture)
                            $typeName = $value.GetType().FullName
                        }
                        $token = "$typeName|$($text.Length)|$text;"
                    }
                    $hasher.AppendData($encoding.GetBytes($token))
                }
                $hasher.AppendData($encoding.GetBytes('R;'))
            }
        }
        finally {
            $reader.Dispose()
        }

        [Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $hasher.Dispose()
        $command.Dispose()
    }
}

function Get-TableSummary {
    param(
        [Parameter(Mandatory)] $Connection,
        [Parameter(Mandatory)] [string] $Table,
        [Parameter(Mandatory)] [string] $OrderBy
    )

    $exists = [long](Invoke-Scalar $Connection (
        "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='$Table';"))
    if ($exists -ne 1) {
        return [ordered]@{
            Exists = $false
            Count = $null
            Columns = @()
            Digest = $null
        }
    }

    $columns = @()
    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = "PRAGMA table_info('$Table');"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $columns += $reader.GetString(1)
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $command.Dispose()
    }

    [ordered]@{
        Exists = $true
        Count = [long](Invoke-Scalar $Connection "SELECT COUNT(*) FROM $Table;")
        Columns = $columns
        Digest = Get-QueryDigest $Connection "SELECT * FROM $Table ORDER BY $OrderBy;"
    }
}

function Get-StageIndexes {
    param([Parameter(Mandatory)] $Connection)

    $indexes = @()
    $indexRows = @()
    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = (
            "SELECT name, [unique], origin, partial " +
            "FROM pragma_index_list('stage_items') ORDER BY name;")
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $indexRows += [pscustomobject]@{
                    Name = $reader.GetString(0)
                    Unique = $reader.GetInt64(1) -ne 0
                    Origin = $reader.GetString(2)
                    Partial = $reader.GetInt64(3) -ne 0
                }
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $command.Dispose()
    }

    foreach ($indexRow in $indexRows) {
        $keyColumns = @()
        $details = $Connection.CreateCommand()
        try {
            $details.CommandText = "SELECT name, coll FROM pragma_index_xinfo('$($indexRow.Name)') WHERE key = 1 ORDER BY seqno;"
            $detailReader = $details.ExecuteReader()
            try {
                while ($detailReader.Read()) {
                    $keyColumns += [ordered]@{
                        Name = if ($detailReader.IsDBNull(0)) { $null } else { $detailReader.GetString(0) }
                        Collation = if ($detailReader.IsDBNull(1)) { $null } else { $detailReader.GetString(1) }
                    }
                }
            }
            finally {
                $detailReader.Dispose()
            }
        }
        finally {
            $details.Dispose()
        }

        $indexes += [ordered]@{
            Name = $indexRow.Name
            Unique = $indexRow.Unique
            Origin = $indexRow.Origin
            Partial = $indexRow.Partial
            KeyColumns = $keyColumns
        }
    }
    $indexes
}

function Test-StageIndexDefinition {
    param(
        [Parameter(Mandatory)] $Audit,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [bool] $Unique,
        [Parameter(Mandatory)] [string[]] $Columns,
        [Parameter(Mandatory)] [string[]] $Collations
    )

    $matches = @($Audit.StageIndexes | Where-Object {
        [StringComparer]::OrdinalIgnoreCase.Equals($_.Name, $Name)
    })
    if ($matches.Count -ne 1 -or
        $matches[0].Unique -ne $Unique -or
        $matches[0].Partial) {
        return $false
    }

    $keys = @($matches[0].KeyColumns)
    if ($keys.Count -ne $Columns.Count -or $keys.Count -ne $Collations.Count) {
        return $false
    }

    for ($index = 0; $index -lt $keys.Count; $index++) {
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
                [string]$keys[$index].Name,
                $Columns[$index]) -or
            -not [StringComparer]::OrdinalIgnoreCase.Equals(
                [string]$keys[$index].Collation,
                $Collations[$index])) {
            return $false
        }
    }

    $true
}

function Test-StageSchemaSevenIndexes {
    param([Parameter(Mandatory)] $Audit)

    (Test-StageIndexDefinition `
        -Audit $Audit `
        -Name 'ux_stage_items_canonical' `
        -Unique $true `
        -Columns @('canonical_key') `
        -Collations @('NOCASE')) -and
    (Test-StageIndexDefinition `
        -Audit $Audit `
        -Name 'ix_stage_items_order' `
        -Unique $false `
        -Columns @('sort_order', 'id') `
        -Collations @('BINARY', 'BINARY'))
}

function Test-StageSchemaEightIndexes {
    param([Parameter(Mandatory)] $Audit)

    $unexpectedUniqueIndexes = @($Audit.StageIndexes | Where-Object {
        $_.Unique -and
        -not [StringComparer]::OrdinalIgnoreCase.Equals($_.Origin, 'pk')
    })
    $oldIndexes = @($Audit.StageIndexes | Where-Object {
        [StringComparer]::OrdinalIgnoreCase.Equals(
            $_.Name,
            'ux_stage_items_canonical')
    })

    $unexpectedUniqueIndexes.Count -eq 0 -and
    $oldIndexes.Count -eq 0 -and
    (Test-StageIndexDefinition `
        -Audit $Audit `
        -Name 'ix_stage_items_canonical' `
        -Unique $false `
        -Columns @('canonical_key') `
        -Collations @('NOCASE')) -and
    (Test-StageIndexDefinition `
        -Audit $Audit `
        -Name 'ix_stage_items_order' `
        -Unique $false `
        -Columns @('sort_order', 'id') `
        -Collations @('BINARY', 'BINARY'))
}

function Get-AssetAudit {
    param(
        [Parameter(Mandatory)] $Connection,
        [Parameter(Mandatory)] [string] $Root
    )

    $absoluteRoot = Resolve-AbsolutePath $Root
    $rootPrefix = $absoluteRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $readyCount = 0L
    $readyBytes = 0L
    $missing = 0L
    $lengthMismatch = 0L
    $hashMismatch = 0L
    $unsafeRelativePath = 0L
    $nonReady = 0L
    $summaryLines = [Collections.Generic.List[string]]::new()

    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = "SELECT hash, relative_path, byte_length, state FROM content_assets ORDER BY hash;"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $hash = $reader.GetString(0)
                $relativePath = $reader.GetString(1)
                $expectedLength = $reader.GetInt64(2)
                $state = $reader.GetInt64(3)
                if ($state -ne 0) {
                    $nonReady++
                    $summaryLines.Add("$hash|state:$state")
                    continue
                }

                $readyCount++
                $readyBytes += $expectedLength
                $candidate = [IO.Path]::GetFullPath((Join-Path $absoluteRoot $relativePath))
                if (-not $candidate.StartsWith(
                        $rootPrefix,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    $unsafeRelativePath++
                    $summaryLines.Add("$hash|unsafe")
                    continue
                }
                if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                    $missing++
                    $summaryLines.Add("$hash|missing")
                    continue
                }

                $actualLength = (Get-Item -LiteralPath $candidate).Length
                if ($actualLength -ne $expectedLength) {
                    $lengthMismatch++
                }
                $actualHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
                if (-not [StringComparer]::OrdinalIgnoreCase.Equals($actualHash, $hash)) {
                    $hashMismatch++
                }
                $summaryLines.Add("$hash|$actualLength|$actualHash")
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $command.Dispose()
    }

    $summaryText = [string]::Join("`n", $summaryLines)
    $summaryBytes = [Text.Encoding]::UTF8.GetBytes($summaryText)
    $aggregate = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($summaryBytes)).ToLowerInvariant()
    [ordered]@{
        ReadyCount = $readyCount
        ReadyBytes = $readyBytes
        NonReadyCount = $nonReady
        MissingCount = $missing
        LengthMismatchCount = $lengthMismatch
        HashMismatchCount = $hashMismatch
        UnsafeRelativePathCount = $unsafeRelativePath
        AggregateDigest = $aggregate
    }
}

function Get-DatabaseAuditFromConnection {
    param(
        [Parameter(Mandatory)] $Connection,
        [string] $Assets
    )

    $quickResults = @()
    $quickCommand = $Connection.CreateCommand()
    try {
        $quickCommand.CommandText = 'PRAGMA quick_check;'
        $reader = $quickCommand.ExecuteReader()
        try {
            while ($reader.Read()) {
                $quickResults += $reader.GetString(0)
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $quickCommand.Dispose()
    }

    $tableOrders = [ordered]@{
        app_settings = 'key'
        launcher_items = 'id'
        clipboard_entries = 'id'
        clipboard_representations = 'entry_id, representation_kind'
        clipboard_files = 'entry_id, ordinal'
        content_assets = 'hash'
        clipboard_asset_refs = 'entry_id, role'
        stage_items = 'id'
    }
    $tables = [ordered]@{}
    foreach ($entry in $tableOrders.GetEnumerator()) {
        $tables[$entry.Key] = Get-TableSummary $Connection $entry.Key $entry.Value
    }

    $assetAudit = $null
    if (-not [string]::IsNullOrWhiteSpace($Assets) -and
        $tables.content_assets.Exists) {
        $assetAudit = Get-AssetAudit $Connection $Assets
    }

    [ordered]@{
        UserVersion = [long](Invoke-Scalar $Connection 'PRAGMA user_version;')
        DataVersion = [long](Invoke-Scalar $Connection 'PRAGMA data_version;')
        JournalMode = [string](Invoke-Scalar $Connection 'PRAGMA journal_mode;')
        QuickCheck = $quickResults
        ForeignKeyViolations = [long](Invoke-Scalar $Connection (
            'SELECT COUNT(*) FROM pragma_foreign_key_check;'))
        SchemaDigest = Get-QueryDigest $Connection (
            "SELECT type, name, tbl_name, coalesce(sql, '') FROM sqlite_schema " +
            "ORDER BY type, name;")
        Tables = $tables
        StageIndexes = if ($tables.stage_items.Exists) {
            @(Get-StageIndexes $Connection)
        }
        else {
            @()
        }
        Assets = $assetAudit
    }
}

function Get-DatabaseAudit {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $Assets
    )

    $absolutePath = Resolve-AbsolutePath $Path
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        throw "Database does not exist: $absolutePath"
    }

    $connection = New-SqliteConnection $absolutePath $true
    try {
        $connection.Open()
        Get-DatabaseAuditFromConnection $connection $Assets
    }
    finally {
        $connection.Dispose()
    }
}

function Get-PreservedState {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [ValidateSet(5, 6)] [int] $ProjectionSchemaVersion,
        [Parameter(Mandatory)] [ValidateSet(5, 6, 7, 8)] [int] $ExpectedDatabaseVersion
    )

    $connection = New-SqliteConnection $Path $true
    try {
        $connection.Open()
        if ([long](Invoke-Scalar $connection 'PRAGMA user_version;') -ne $ExpectedDatabaseVersion) {
            throw "Expected database schema $ExpectedDatabaseVersion while building preserved state."
        }

        $queries = [ordered]@{
            app_settings = 'SELECT * FROM app_settings ORDER BY key;'
            launcher_items = 'SELECT * FROM launcher_items ORDER BY id;'
            clipboard_representations = (
                'SELECT * FROM clipboard_representations ORDER BY entry_id, representation_kind;')
            clipboard_files = 'SELECT * FROM clipboard_files ORDER BY entry_id, ordinal;'
            content_assets = 'SELECT * FROM content_assets ORDER BY hash;'
            clipboard_asset_refs = (
                'SELECT * FROM clipboard_asset_refs ORDER BY entry_id, role;')
        }
        if ($ProjectionSchemaVersion -eq 5) {
            $queries.clipboard_entries = @'
SELECT id, kind, semantic_hash, plain_preview,
       first_captured_utc, last_captured_utc, last_capture_order,
       capture_count, is_pinned, revision, file_intent, item_count,
       pixel_width, pixel_height, has_alpha, dpi_x_milli, dpi_y_milli
FROM clipboard_entries
ORDER BY id;
'@
        }
        else {
            $queries.clipboard_entries = 'SELECT * FROM clipboard_entries ORDER BY id;'
        }

        $state = [ordered]@{}
        foreach ($entry in $queries.GetEnumerator()) {
            $state[$entry.Key] = Get-QueryDigest $connection $entry.Value
        }
        $state
    }
    finally {
        $connection.Dispose()
    }
}

function Invoke-ProductInitialization {
    param([Parameter(Mandatory)] [string] $Path)

    $options = [Vok.Core.Stage.StageOptions]::new()
    $repository = [Vok.Infrastructure.Windows.Persistence.SqliteStageRepository]::new(
        (Resolve-AbsolutePath $Path),
        $options)
    try {
        $task = $repository.LoadPageAsync(
            $null,
            1,
            [Threading.CancellationToken]::None)
        [void]$task.GetAwaiter().GetResult()
    }
    finally {
        $repository.Dispose()
    }
}

function Invoke-AuditMode {
    Set-ReportStage 'AuditDatabase'
    $script:Report.Database = Get-DatabaseAudit $DatabasePath $AssetRoot
}

function Invoke-BackupMode {
    if ([string]::IsNullOrWhiteSpace($BackupPath) -or
        [string]::IsNullOrWhiteSpace($AllowedMutationRoot)) {
        throw 'Backup mode requires BackupPath and AllowedMutationRoot.'
    }

    $sourcePath = Resolve-AbsolutePath $DatabasePath
    $targetPath = Resolve-AbsolutePath $BackupPath
    $mutationRoot = Resolve-AbsolutePath $AllowedMutationRoot
    if (-not (Test-PathWithinRoot $targetPath $mutationRoot)) {
        throw "Backup target is outside AllowedMutationRoot: $targetPath"
    }
    if ([StringComparer]::OrdinalIgnoreCase.Equals($sourcePath, $targetPath)) {
        throw 'Backup target must not equal the source database.'
    }
    if (Test-Path -LiteralPath $targetPath) {
        throw "Backup target already exists: $targetPath"
    }
    $targetParent = Split-Path -Parent $targetPath
    if (-not (Test-Path -LiteralPath $targetParent -PathType Container)) {
        throw "Backup target directory does not exist: $targetParent"
    }

    Set-ReportStage 'OpenBackupSource'
    $source = New-SqliteConnection $sourcePath $true
    $destination = $null
    try {
        $source.Open()
        $before = Get-DatabaseAuditFromConnection $source $AssetRoot
        Set-ReportStage 'OnlineBackup'
        $destination = New-SqliteConnection $targetPath $false
        $destination.Open()
        $source.BackupDatabase($destination)
        $destination.Close()
        $destination.Dispose()
        $destination = $null
        $afterDataVersion = [long](Invoke-Scalar $source 'PRAGMA data_version;')
        $after = if ($before.DataVersion -eq $afterDataVersion) {
            Get-DatabaseAuditFromConnection $source $AssetRoot
        }
        else {
            $null
        }
    }
    catch {
        foreach ($partialPath in @($targetPath, "$targetPath-wal", "$targetPath-shm")) {
            if (Test-Path -LiteralPath $partialPath -PathType Leaf) {
                Remove-Item -LiteralPath $partialPath -Force
            }
        }
        throw
    }
    finally {
        if ($null -ne $destination) {
            $destination.Dispose()
        }
        $source.Dispose()
    }

    Set-ReportStage 'AuditBackup'
    $backupAudit = Get-DatabaseAudit $targetPath $AssetRoot
    $sourceStable = $before.DataVersion -eq $afterDataVersion
    $exactMatch = $false
    if ($sourceStable) {
        $exactMatch = (
            $after.SchemaDigest -eq $backupAudit.SchemaDigest -and
            $after.Tables.app_settings.Digest -eq $backupAudit.Tables.app_settings.Digest -and
            $after.Tables.launcher_items.Digest -eq $backupAudit.Tables.launcher_items.Digest -and
            $after.Tables.clipboard_entries.Digest -eq $backupAudit.Tables.clipboard_entries.Digest -and
            $after.Tables.clipboard_representations.Digest -eq $backupAudit.Tables.clipboard_representations.Digest -and
            $after.Tables.clipboard_files.Digest -eq $backupAudit.Tables.clipboard_files.Digest -and
            $after.Tables.content_assets.Digest -eq $backupAudit.Tables.content_assets.Digest -and
            $after.Tables.clipboard_asset_refs.Digest -eq $backupAudit.Tables.clipboard_asset_refs.Digest -and
            $after.Tables.stage_items.Digest -eq $backupAudit.Tables.stage_items.Digest)
    }

    $script:Report.Backup = [ordered]@{
        SourceBefore = $before
        SourceDataVersionAfter = $afterDataVersion
        SourceStable = $sourceStable
        SourceAfter = $after
        Backup = $backupAudit
        ExactLogicalMatchWhenStable = $exactMatch
        BackupFileBytes = (Get-Item -LiteralPath $targetPath).Length
    }
    if ($backupAudit.QuickCheck.Count -ne 1 -or
        $backupAudit.QuickCheck[0] -ne 'ok' -or
        $backupAudit.ForeignKeyViolations -ne 0 -or
        ($sourceStable -and -not $exactMatch)) {
        throw 'The online backup did not pass its consistency checks.'
    }
}

function Assert-ReplayPath {
    if ([string]::IsNullOrWhiteSpace($AllowedMutationRoot)) {
        throw "$Mode requires AllowedMutationRoot."
    }
    $absolutePath = Resolve-AbsolutePath $DatabasePath
    $mutationRoot = Resolve-AbsolutePath $AllowedMutationRoot
    if (-not (Test-PathWithinRoot $absolutePath $mutationRoot)) {
        throw "Replay database is outside AllowedMutationRoot: $absolutePath"
    }
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        throw "Replay database does not exist: $absolutePath"
    }
    $absolutePath
}

function Compare-PreservedState {
    param(
        [Parameter(Mandatory)] $Before,
        [Parameter(Mandatory)] $After
    )

    $changed = @()
    foreach ($key in $Before.Keys) {
        if (-not $After.Contains($key) -or $Before[$key] -ne $After[$key]) {
            $changed += $key
        }
    }
    $changed
}

function Invoke-ReplaySchema6Mode {
    $path = Assert-ReplayPath
    Set-ReportStage 'AuditCurrentSourceCopy'
    $sourceAudit = Get-DatabaseAudit $path $AssetRoot
    if ($sourceAudit.UserVersion -notin @(7, 8) -or
        -not $sourceAudit.Tables.stage_items.Exists) {
        throw 'ReplaySchema6 requires a schema 7 or 8 database copy with stage_items.'
    }

    Set-ReportStage 'DeriveSchema6Copy'
    $connection = New-SqliteConnection $path $false
    try {
        $connection.Open()
        $transaction = $connection.BeginTransaction()
        try {
            $command = $connection.CreateCommand()
            try {
                $command.Transaction = $transaction
                $command.CommandText = 'DROP TABLE stage_items; PRAGMA user_version = 6;'
                [void]$command.ExecuteNonQuery()
            }
            finally {
                $command.Dispose()
            }
            $transaction.Commit()
        }
        catch {
            $transaction.Rollback()
            throw
        }
        finally {
            $transaction.Dispose()
        }
    }
    finally {
        $connection.Dispose()
    }

    $derivedAudit = Get-DatabaseAudit $path $AssetRoot
    if ($derivedAudit.UserVersion -ne 6 -or $derivedAudit.Tables.stage_items.Exists) {
        throw 'Unable to derive the schema 6 replay copy.'
    }
    $preservedBefore = Get-PreservedState $path 6 6

    Set-ReportStage 'InitializeSchema6CopyFirstPass'
    Invoke-ProductInitialization $path
    Set-ReportStage 'InitializeSchema6CopySecondPass'
    Invoke-ProductInitialization $path
    Set-ReportStage 'AuditSchema6ReplayResult'
    $resultAudit = Get-DatabaseAudit $path $AssetRoot
    $preservedAfter = Get-PreservedState $path 6 8
    $changed = @(Compare-PreservedState $preservedBefore $preservedAfter)

    $script:Report.Replay = [ordered]@{
        SourceSchema = $sourceAudit
        DerivedSchema6 = $derivedAudit
        ResultSchema8 = $resultAudit
        OriginalStageCountRemovedFromDerivedCopy = $sourceAudit.Tables.stage_items.Count
        ChangedPreservedTables = $changed
    }
    if ($resultAudit.UserVersion -ne 8 -or
        -not $resultAudit.Tables.stage_items.Exists -or
        $resultAudit.Tables.stage_items.Count -ne 0 -or
        -not (Test-StageSchemaEightIndexes $resultAudit) -or
        $resultAudit.QuickCheck.Count -ne 1 -or
        $resultAudit.QuickCheck[0] -ne 'ok' -or
        $resultAudit.ForeignKeyViolations -ne 0 -or
        $changed.Count -ne 0) {
        throw 'The derived schema 6 replay did not preserve the expected state.'
    }
}

function Invoke-ReplaySchema7Mode {
    $path = Assert-ReplayPath
    Set-ReportStage 'AuditSchema7CopyBefore'
    $before = Get-DatabaseAudit $path $AssetRoot
    if ($before.UserVersion -ne 7 -or
        -not $before.Tables.stage_items.Exists -or
        -not (Test-StageSchemaSevenIndexes $before)) {
        throw 'ReplaySchema7 requires a valid schema 7 database copy.'
    }

    Set-ReportStage 'InitializeSchema7CopyFirstPass'
    Invoke-ProductInitialization $path
    Set-ReportStage 'AuditSchema7MigrationFirstPass'
    $afterFirst = Get-DatabaseAudit $path $AssetRoot
    Set-ReportStage 'InitializeSchema7CopySecondPass'
    Invoke-ProductInitialization $path
    Set-ReportStage 'AuditSchema7MigrationSecondPass'
    $after = Get-DatabaseAudit $path $AssetRoot

    $changed = @()
    foreach ($key in $before.Tables.Keys) {
        if ($before.Tables[$key].Digest -ne $after.Tables[$key].Digest) {
            $changed += $key
        }
    }
    $script:Report.Replay = [ordered]@{
        BeforeSchema7 = $before
        AfterFirstSchema8 = $afterFirst
        AfterSecondSchema8 = $after
        ChangedTables = $changed
        StageRowsPreserved = (
            $before.Tables.stage_items.Digest -eq $after.Tables.stage_items.Digest)
        SecondPassSchemaDigestUnchanged = (
            $afterFirst.SchemaDigest -eq $after.SchemaDigest)
    }
    if ($afterFirst.UserVersion -ne 8 -or
        $after.UserVersion -ne 8 -or
        -not (Test-StageSchemaEightIndexes $afterFirst) -or
        -not (Test-StageSchemaEightIndexes $after) -or
        $afterFirst.QuickCheck.Count -ne 1 -or
        $afterFirst.QuickCheck[0] -ne 'ok' -or
        $afterFirst.ForeignKeyViolations -ne 0 -or
        $after.QuickCheck.Count -ne 1 -or
        $after.QuickCheck[0] -ne 'ok' -or
        $after.ForeignKeyViolations -ne 0 -or
        $changed.Count -ne 0 -or
        $before.Tables.stage_items.Digest -ne $after.Tables.stage_items.Digest -or
        $afterFirst.SchemaDigest -ne $after.SchemaDigest) {
        throw 'The schema 7 to 8 migration did not preserve data or remain idempotent.'
    }
}

function Invoke-ReplaySchema8Mode {
    $path = Assert-ReplayPath
    Set-ReportStage 'AuditSchema8CopyBefore'
    $before = Get-DatabaseAudit $path $AssetRoot
    if ($before.UserVersion -ne 8 -or
        -not $before.Tables.stage_items.Exists -or
        -not (Test-StageSchemaEightIndexes $before)) {
        throw 'ReplaySchema8 requires a valid schema 8 database copy.'
    }

    Set-ReportStage 'InitializeSchema8CopyFirstPass'
    Invoke-ProductInitialization $path
    Set-ReportStage 'InitializeSchema8CopySecondPass'
    Invoke-ProductInitialization $path
    Set-ReportStage 'AuditSchema8CopyAfter'
    $after = Get-DatabaseAudit $path $AssetRoot

    $changed = @()
    foreach ($key in $before.Tables.Keys) {
        if ($before.Tables[$key].Digest -ne $after.Tables[$key].Digest) {
            $changed += $key
        }
    }
    $script:Report.Replay = [ordered]@{
        Before = $before
        After = $after
        ChangedTables = $changed
        SchemaDigestUnchanged = $before.SchemaDigest -eq $after.SchemaDigest
    }
    if ($after.UserVersion -ne 8 -or
        -not (Test-StageSchemaEightIndexes $after) -or
        $after.QuickCheck.Count -ne 1 -or
        $after.QuickCheck[0] -ne 'ok' -or
        $after.ForeignKeyViolations -ne 0 -or
        $changed.Count -ne 0 -or
        $before.SchemaDigest -ne $after.SchemaDigest) {
        throw 'The schema 8 replay was not idempotent.'
    }
}

function Invoke-ReplaySchema5Mode {
    $path = Assert-ReplayPath
    Set-ReportStage 'AuditSchema5SourceCopy'
    $sourceAudit = Get-DatabaseAudit $path $AssetRoot
    if ($sourceAudit.UserVersion -ne 5 -or $sourceAudit.Tables.stage_items.Exists) {
        throw 'ReplaySchema5 requires a schema 5 database copy without stage_items.'
    }
    $preservedBefore = Get-PreservedState $path 5 5

    Set-ReportStage 'InitializeSchema5CopyFirstPass'
    Invoke-ProductInitialization $path
    Set-ReportStage 'InitializeSchema5CopySecondPass'
    Invoke-ProductInitialization $path
    Set-ReportStage 'AuditSchema5ReplayResult'
    $resultAudit = Get-DatabaseAudit $path $AssetRoot
    $preservedAfter = Get-PreservedState $path 5 8
    $changed = @(Compare-PreservedState $preservedBefore $preservedAfter)

    $script:Report.Replay = [ordered]@{
        SourceSchema5 = $sourceAudit
        ResultSchema8 = $resultAudit
        ChangedPreservedTables = $changed
    }
    if ($resultAudit.UserVersion -ne 8 -or
        -not $resultAudit.Tables.stage_items.Exists -or
        $resultAudit.Tables.stage_items.Count -ne 0 -or
        -not (Test-StageSchemaEightIndexes $resultAudit) -or
        $resultAudit.QuickCheck.Count -ne 1 -or
        $resultAudit.QuickCheck[0] -ne 'ok' -or
        $resultAudit.ForeignKeyViolations -ne 0 -or
        $changed.Count -ne 0) {
        throw 'The schema 5 replay did not preserve the expected state.'
    }
}

Save-Report
try {
    Set-ReportStage 'LoadAssemblies'
    Import-VokSqliteAssemblies
    switch ($Mode) {
        'Audit' { Invoke-AuditMode }
        'Backup' { Invoke-BackupMode }
        'ReplaySchema8' { Invoke-ReplaySchema8Mode }
        'ReplaySchema7' { Invoke-ReplaySchema7Mode }
        'ReplaySchema6' { Invoke-ReplaySchema6Mode }
        'ReplaySchema5' { Invoke-ReplaySchema5Mode }
    }

    $script:Report.Status = 'Succeeded'
    $script:Report.Stage = 'Completed'
    $script:Report.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Save-Report
}
catch {
    $script:Report.Status = 'Failed'
    $script:Report.FailedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $script:Report.Error = [ordered]@{
        Type = $_.Exception.GetType().FullName
        Message = $_.Exception.Message
        HResult = $_.Exception.HResult
    }
    Save-Report
    throw
}
finally {
    if ($script:NativeSearchPathSet) {
        [void][VokM3NativeLibraryPath]::SetDllDirectory($null)
    }
}
