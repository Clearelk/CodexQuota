using CodexQuota.Core.Services;
using System.Security.Cryptography;
using CodexQuota.Core.Models;

namespace CodexQuota.Core.LocalUsage;

/// <summary>Per-cycle and retained-state limits for local history processing.</summary>
public sealed class LocalUsageScanOptions
{
    public int MaxFilesDiscovered { get; init; } = 2000;
    public long MaxBytesPerCycle { get; init; } = 96L * 1024 * 1024;
    public int MaxLineBytes { get; init; } = 4 * 1024 * 1024;
    public int ReadBufferBytes { get; init; } = 64 * 1024;
    public int MaxTrackedFiles { get; init; } = 8192;
    public int MaxRetainedTailBytes { get; init; } = 8 * 1024 * 1024;
    public static LocalUsageScanOptions Default { get; } = new();
}

/// <summary>
/// Single-caller, bounded incremental scanner. Only allowed-field projections, hashes,
/// numeric cursors and bounded transient unfinished frames are retained in memory.
/// Discovery continues across cycles; a partial sweep never evicts its earlier results.
/// </summary>
public sealed class LocalUsageScanner : IDisposable
{
    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly LocalUsageScanOptions _options;
    private readonly Dictionary<string, TrackedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private IEnumerator<string>? _discovery;
    private DateOnly _aggregateDate;
    private int _folderIndex;
    private int _sweep;
    private int _nextRead;
    private int _pendingBytes;
    private bool _discovering;
    private bool _rootsPresent;
    private bool _discoveryFailed;
    private bool _stateLimited;
    private bool _sweepLimited;
    private bool _removedToday;

    public LocalUsageScanner(string root, TimeProvider? timeProvider = null, LocalUsageScanOptions? options = null)
    {
        _root = root;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? LocalUsageScanOptions.Default;
        if (_options.MaxFilesDiscovered <= 0 || _options.MaxBytesPerCycle <= 0 ||
            _options.MaxLineBytes <= 0 || _options.ReadBufferBytes <= 0 ||
            _options.MaxTrackedFiles <= 0 || _options.MaxRetainedTailBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public Task<LocalUsageSnapshot> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private LocalUsageSnapshot Scan(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset localNow = _time.GetLocalNow();
        DateOnly today = DateOnly.FromDateTime(localNow.Date);
        if (_aggregateDate != today)
        {
            Dispose();
            _files.Clear();
            _pendingBytes = 0;
            _removedToday = false;
            _stateLimited = false;
            _aggregateDate = today;
        }

        DateTime startUtc = new DateTimeOffset(localNow.Date, _time.LocalTimeZone.GetUtcOffset(localNow.Date)).UtcDateTime;
        try
        {
            Discover(startUtc, cancellationToken);
            if (!_rootsPresent && !_discovering)
            {
                return LocalUsageSnapshot.Unavailable(today, _time.GetUtcNow(), new AppMessage("LocalMissingRoot"));
            }

            long cycleBytes = 0;
            long budget = _options.MaxBytesPerCycle;
            bool bounded = _discovering || _discoveryFailed || _stateLimited;
            TrackedFile[] active = _files.Values.ToArray();
            int inspected = Math.Min(active.Length, _options.MaxFilesDiscovered);
            int start = active.Length == 0 ? 0 : _nextRead % active.Length;
            for (int i = 0; i < inspected; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadAppended(active[(start + i) % active.Length], ref budget, ref cycleBytes, cancellationToken, ref bounded);
            }
            // Rotate even when a huge earlier file consumes the byte budget.
            _nextRead = active.Length == 0 ? 0 : (start + 1) % active.Length;
            foreach (TrackedFile file in active)
            {
                if (file.Missing)
                {
                    _removedToday |= file.Tokens > 0;
                    Remove(file);
                }
                else if (file.Dirty || !file.Opened || file.Offset < file.KnownLength) { bounded = true; }
            }

            DateOnly finishDate = DateOnly.FromDateTime(_time.GetLocalNow().Date);
            if (finishDate != today)
            {
                Dispose();
                return LocalUsageSnapshot.Unavailable(finishDate, _time.GetUtcNow(), new AppMessage("LocalDateChanged"));
            }
            return BuildSnapshot(today, _time.GetUtcNow(), cycleBytes, bounded);
        }
        catch (OperationCanceledException) { Dispose(); throw; }
        catch (IOException) { Dispose(); return LocalUsageSnapshot.Unavailable(today, _time.GetUtcNow(), new AppMessage("LocalReadFailure")); }
        catch (UnauthorizedAccessException) { Dispose(); return LocalUsageSnapshot.Unavailable(today, _time.GetUtcNow(), new AppMessage("LocalReadDenied")); }
    }

    private void Discover(DateTime todayStartUtc, CancellationToken cancellationToken)
    {
        if (!_discovering)
        {
            _discovering = true;
            _folderIndex = 0;
            _sweep++;
            _rootsPresent = false;
            _discoveryFailed = false;
            _sweepLimited = false;
        }

        int examined = 0;
        while (examined < _options.MaxFilesDiscovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_discovery is null)
            {
                if (_folderIndex >= 2)
                {
                    _discovering = false;
                    _stateLimited = _sweepLimited;
                    if (!_discoveryFailed)
                    {
                        foreach (TrackedFile file in _files.Values.Where(file => file.SeenSweep != _sweep).ToArray())
                        {
                            _removedToday |= file.Tokens > 0;
                            Remove(file);
                        }
                    }
                    return;
                }

                string folder = _folderIndex++ == 0 ? LocalUsagePaths.SessionsFolder : LocalUsagePaths.ArchivedSessionsFolder;
                string directory = Path.Combine(_root, folder);
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0) { _discoveryFailed = true; continue; }
                    _rootsPresent = true;
                    _discovery = Directory.EnumerateFiles(directory, "*.jsonl", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = false,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    }).GetEnumerator();
                }
                catch (DirectoryNotFoundException) { continue; }
                catch (FileNotFoundException) { continue; }
                catch (IOException) { _discoveryFailed = true; continue; }
                catch (UnauthorizedAccessException) { _discoveryFailed = true; continue; }
            }

            try
            {
                if (!_discovery.MoveNext())
                {
                    _discovery.Dispose();
                    _discovery = null;
                    continue;
                }
                examined++;
                string path = _discovery.Current;
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < todayStartUtc) { continue; }
                if (!_files.TryGetValue(path, out TrackedFile? file))
                {
                    if (_files.Count >= _options.MaxTrackedFiles) { _stateLimited = true; _sweepLimited = true; continue; }
                    file = new TrackedFile(path);
                    _files.Add(path, file);
                }
                file.SeenSweep = _sweep;
                file.KnownLength = info.Length;
                file.Dirty |= !file.Opened || info.CreationTimeUtc != file.CreationUtc || info.LastWriteTimeUtc != file.LastWriteUtc || info.Length != file.Offset;
            }
            catch (IOException) { _discoveryFailed = true; _discovery?.Dispose(); _discovery = null; }
            catch (UnauthorizedAccessException) { _discoveryFailed = true; _discovery?.Dispose(); _discovery = null; }
        }
    }

    private void ReadAppended(TrackedFile file, ref long budget, ref long bytes, CancellationToken cancellation, ref bool bounded)
    {
        try
        {
            var info = new FileInfo(file.Path);
            info.Refresh();
            if (!info.Exists) { file.Missing = true; return; }
            file.KnownLength = info.Length;
            bool changed = file.Opened && (info.CreationTimeUtc != file.CreationUtc || info.LastWriteTimeUtc != file.LastWriteUtc);
            if (file.Opened && info.Length == file.Offset && !changed)
            {
                file.ReadFailed = false;
                file.Dirty = false;
                return;
            }
            if (budget <= 0) { bounded = true; return; }
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            long length = stream.Length;
            bool replaced = file.Opened && (length < file.Offset || info.CreationTimeUtc != file.CreationUtc ||
                (changed && length == file.Offset));
            if (!replaced && file.Offset > 0 && length > file.Offset)
            {
                int validationBytes = file.HeadLength + file.AnchorLength;
                if (budget < validationBytes) { bounded = true; return; }
                replaced = !Matches(stream, 0, file.HeadLength, file.HeadHash, ref budget, ref bytes) ||
                    !Matches(stream, file.Offset - file.AnchorLength, file.AnchorLength, file.AnchorHash, ref budget, ref bytes);
            }
            if (replaced) { Reset(file); }
            file.Opened = true;
            file.ReadFailed = false;
            file.CreationUtc = info.CreationTimeUtc;
            file.LastWriteUtc = info.LastWriteTimeUtc;
            file.KnownLength = length;
            file.Dirty = false;
            stream.Position = file.Offset;
            var buffer = new byte[_options.ReadBufferBytes];
            while (file.Offset < length)
            {
                cancellation.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(buffer.Length, Math.Min(length - file.Offset, budget));
                if (wanted <= 0) { bounded = true; break; }
                int read = stream.Read(buffer, 0, wanted);
                if (read == 0) { file.ReadFailed = true; break; }
                if (file.Offset == 0)
                {
                    file.HeadLength = Math.Min(read, 64);
                    file.HeadHash = SHA256.HashData(buffer.AsSpan(0, file.HeadLength));
                }
                file.AnchorLength = Math.Min(read, 64);
                file.AnchorHash = SHA256.HashData(buffer.AsSpan(read - file.AnchorLength, file.AnchorLength));
                file.Offset += read;
                budget -= read;
                bytes += read;
                ProcessChunk(file, buffer.AsSpan(0, read));
            }
        }
        catch (FileNotFoundException) { file.Missing = true; }
        catch (DirectoryNotFoundException) { file.Missing = true; }
        catch (IOException) { file.ReadFailed = true; }
        catch (UnauthorizedAccessException) { file.ReadFailed = true; }
    }

    private static bool Matches(FileStream stream, long position, int length, byte[] hash, ref long budget, ref long bytes)
    {
        Span<byte> buffer = stackalloc byte[64];
        stream.Position = position;
        int read = stream.Read(buffer[..length]);
        budget -= read;
        bytes += read;
        return read == length && SHA256.HashData(buffer[..read]).AsSpan().SequenceEqual(hash);
    }

    private void ProcessChunk(TrackedFile file, ReadOnlySpan<byte> chunk)
    {
        while (!chunk.IsEmpty)
        {
            int newline = chunk.IndexOf((byte)'\n');
            ReadOnlySpan<byte> segment = newline < 0 ? chunk : chunk[..newline];
            if (!file.DroppingLine)
            {
                int length = file.Pending.Length + segment.Length;
                if (length > _options.MaxLineBytes || (newline < 0 && _pendingBytes + segment.Length > _options.MaxRetainedTailBytes))
                {
                    SetPending(file, []);
                    file.DroppingLine = true;
                    file.OversizedLine = true;
                }
                else
                {
                    byte[]? merged = null;
                    ReadOnlySpan<byte> frame = segment;
                    if (file.Pending.Length > 0)
                    {
                        merged = new byte[length];
                        file.Pending.CopyTo(merged, 0);
                        segment.CopyTo(merged.AsSpan(file.Pending.Length));
                        frame = merged;
                    }
                    if (newline >= 0)
                    {
                        if (!frame.IsEmpty && frame[^1] == (byte)'\r') { frame = frame[..^1]; }
                        ProcessLine(file, frame);
                        SetPending(file, []);
                    }
                    else { SetPending(file, merged ?? segment.ToArray()); }
                }
            }
            if (newline < 0) { return; }
            file.DroppingLine = false;
            chunk = chunk[(newline + 1)..];
        }
    }

    private void ProcessLine(TrackedFile file, ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty) { return; }
        LocalRolloutRecord record = CodexRolloutDecoder.Decode(line);
        if (record.Kind == LocalRolloutKind.Invalid) { file.Ambiguous = true; return; }
        if (record.Kind == LocalRolloutKind.SessionMeta)
        {
            if (file.SessionKey is not null && record.SessionId != file.SessionKey) { file.Ambiguous = true; file.Tokens = 0; file.Baseline = null; }
            file.SessionKey = record.SessionId;
            file.ProviderKnown = record.Provider is not null;
            file.Excluded = record.Provider is not null && record.Provider != "openai";
            return;
        }
        if (record.Kind != LocalRolloutKind.TokenCount || file.Excluded) { return; }
        file.Ambiguous |= record.InvalidUsage || file.SessionKey is null || !file.ProviderKnown;
        if (record.Timestamp is not { } timestamp) { file.Ambiguous = true; return; }
        DateOnly date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, _time.LocalTimeZone).Date);
        if (file.PreviousTimestamp is { } previousStamp && timestamp < previousStamp) { file.Ambiguous = true; return; }
        file.PreviousTimestamp = timestamp;
        ApplyTokenEvent(file, date == _aggregateDate, record.CumulativeTotal, record.LastTotal);
    }

    private static void ApplyTokenEvent(TrackedFile file, bool today, long? cumulative, long? last)
    {
        // A last-turn counter without a cumulative identity can be replayed. Do not add it:
        // a later valid cumulative delta already includes that usage once.
        if (cumulative is null) { if (today) { file.Ambiguous = true; } return; }
        long contribution = 0;
        if (file.Baseline is not { } prior)
        {
            if (today)
            {
                contribution = last ?? 0;
                file.Ambiguous |= last is null || cumulative > last;
            }
        }
        else if (cumulative < prior)
        {
            if (today) { contribution = last ?? 0; file.Ambiguous = true; }
        }
        else if (today) { contribution = cumulative.Value - prior; }
        file.Baseline = cumulative;
        file.Tokens = SaturatingAdd(file.Tokens, contribution, out bool overflow);
        file.Ambiguous |= overflow;
    }

    private void Reset(TrackedFile file)
    {
        SetPending(file, []);
        file.Offset = 0;
        file.Baseline = null;
        file.Tokens = 0;
        file.SessionKey = null;
        file.ProviderKnown = false;
        file.Excluded = false;
        file.PreviousTimestamp = null;
        file.DroppingLine = false;
        file.OversizedLine = false;
        file.Ambiguous = true; // Removed history cannot be claimed as complete today's usage.
    }

    private void SetPending(TrackedFile file, byte[] pending)
    {
        _pendingBytes += pending.Length - file.Pending.Length;
        file.Pending = pending;
    }

    private void Remove(TrackedFile file) { SetPending(file, []); _files.Remove(file.Path); }

    private LocalUsageSnapshot BuildSnapshot(DateOnly today, DateTimeOffset now, long bytes, bool bounded)
    {
        bool readFailed = _discoveryFailed;
        bool attribution = _removedToday;
        bool oversized = false;
        bool duplicated = false;
        int inspected = 0;
        int skipped = 0;
        var groups = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (TrackedFile file in _files.Values)
        {
            readFailed |= file.ReadFailed;
            if (file.Excluded) { skipped++; continue; }
            if (file.Opened) { inspected++; }
            attribution |= file.Ambiguous || file.Pending.Length > 0 || file.DroppingLine;
            oversized |= file.OversizedLine;
            string key = file.SessionKey ?? file.Path;
            if (groups.TryGetValue(key, out long prior))
            {
                duplicated = true;
                skipped++;
                groups[key] = Math.Max(prior, file.Tokens);
            }
            else { groups.Add(key, file.Tokens); }
        }
        // Every copy is read incrementally. A max of overlapping per-file observations is
        // a lower bound, not proof of a complete merge of divergent continuations.
        attribution |= duplicated;
        long total = 0;
        int sessions = 0;
        foreach (long value in groups.Values)
        {
            if (value <= 0) { continue; }
            sessions++;
            total = SaturatingAdd(total, value, out bool overflow);
            attribution |= overflow;
        }

        LocalUsageCoverage coverage;
        AppMessage? note;
        if (readFailed && !_files.Values.Any(file => file.Opened))
        {
            coverage = LocalUsageCoverage.Unavailable;
            note = new AppMessage("LocalReadDenied");
        }
        else if (readFailed) { coverage = LocalUsageCoverage.Partial; note = new AppMessage("LocalPartialRead"); }
        else if (bounded) { coverage = LocalUsageCoverage.Partial; note = new AppMessage("LocalBounded"); }
        else if (oversized) { coverage = LocalUsageCoverage.Partial; note = new AppMessage("LocalOversized"); }
        else if (duplicated) { coverage = LocalUsageCoverage.Partial; note = new AppMessage("LocalDuplicated"); }
        else if (attribution) { coverage = LocalUsageCoverage.Partial; note = new AppMessage("LocalAttribution"); }
        else { coverage = LocalUsageCoverage.Available; note = sessions == 0 ? new AppMessage("LocalEmpty") : null; }
        return new LocalUsageSnapshot(today, sessions, total, coverage, note?.Format(), now, inspected, skipped, bytes) { CoverageMessage = note };
    }

    private static long SaturatingAdd(long left, long right, out bool overflow)
    {
        overflow = right > 0 && left > long.MaxValue - right;
        return overflow ? long.MaxValue : left + right;
    }

    public void Dispose()
    {
        _discovery?.Dispose();
        _discovery = null;
        _discovering = false;
    }

    private sealed class TrackedFile(string path)
    {
        public string Path { get; } = path;
        public int SeenSweep;
        public DateTime CreationUtc;
        public DateTime LastWriteUtc;
        public long KnownLength;
        public long Offset;
        public byte[] Pending = [];
        public bool DroppingLine;
        public byte[] HeadHash = [];
        public int HeadLength;
        public byte[] AnchorHash = [];
        public int AnchorLength;
        public string? SessionKey;
        public bool ProviderKnown;
        public bool Excluded;
        public bool Opened;
        public bool Dirty;
        public bool Missing;
        public bool ReadFailed;
        public bool OversizedLine;
        public bool Ambiguous;
        public long? Baseline;
        public DateTimeOffset? PreviousTimestamp;
        public long Tokens;
    }
}
