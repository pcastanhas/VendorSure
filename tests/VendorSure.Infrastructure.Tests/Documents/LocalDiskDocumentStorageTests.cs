using Microsoft.Extensions.Logging.Abstractions;
using VendorSure.Infrastructure.Documents;
using VendorSure.Services.Documents;

namespace VendorSure.Infrastructure.Tests.Documents;

/// <summary>
/// Unit tests for <see cref="LocalDiskDocumentStorage"/>. Pure filesystem
/// behaviour against a per-test temp directory; no DB. Settings come from
/// a <see cref="FakeSettingsRepository"/>.
///
/// Each test creates its temp directory in <see cref="InitializeAsync"/>
/// and recursively deletes it in <see cref="DisposeAsync"/>; failures
/// shouldn't leak directories under the system temp path.
///
/// NAS integration test is deferred — when a NAS path is available in the
/// dev environment we can add an env-var-gated test that exercises the
/// same surface against the real share.
/// </summary>
public sealed class LocalDiskDocumentStorageTests : IAsyncLifetime
{
    private const string DefaultAllowList = "pdf,jpg,jpeg,png,gif,webp,txt";
    private const long DefaultMaxBytes = 10 * 1024 * 1024; // 10 MB

    private string _tempRoot = string.Empty;
    private FakeSettingsRepository _settings = null!;
    private LocalDiskDocumentStorage _storage = null!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "vendorsure-storage-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _settings = new FakeSettingsRepository();
        _settings.Set("Storage.BasePath", _tempRoot);
        _settings.Set("Storage.AllowedFileExtensions", DefaultAllowList);
        _settings.Set("Storage.MaxFileSizeBytes", DefaultMaxBytes.ToString());

        _storage = new LocalDiskDocumentStorage(
            _settings,
            NullLogger<LocalDiskDocumentStorage>.Instance);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        return Task.CompletedTask;
    }

    // ---------- Store / retrieve round-trip ----------

    [Fact]
    public async Task StoreAsync_writes_file_under_request_directory()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var input = new MemoryStream(bytes);

        var result = await _storage.StoreAsync(42, "report.pdf", input);

        Assert.Equal(StoreDocumentOutcome.Stored, result.Outcome);
        Assert.Equal(bytes.Length, result.SizeBytes);
        Assert.Equal("pdf", result.Extension);

        var expectedPath = Path.Combine(_tempRoot, "42", "report.pdf");
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(expectedPath));
    }

    [Fact]
    public async Task RetrieveAsync_round_trips_stored_bytes()
    {
        var bytes = new byte[] { 10, 20, 30, 40 };
        using (var input = new MemoryStream(bytes))
        {
            await _storage.StoreAsync(7, "data.txt", input);
        }

        var result = await _storage.RetrieveAsync(7, "data.txt");

        Assert.Equal(RetrieveDocumentOutcome.Retrieved, result.Outcome);
        Assert.NotNull(result.Content);
        await using var content = result.Content!;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        Assert.Equal(bytes, buffer.ToArray());
    }

    [Fact]
    public async Task RetrieveAsync_returns_NotFound_when_file_absent()
    {
        var result = await _storage.RetrieveAsync(99, "missing.pdf");

        Assert.Equal(RetrieveDocumentOutcome.NotFound, result.Outcome);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task StoreAsync_overwrites_existing_file_with_same_name()
    {
        var first = new byte[] { 1, 2, 3 };
        var second = new byte[] { 9, 8, 7, 6, 5 };

        using (var input = new MemoryStream(first))
        {
            await _storage.StoreAsync(1, "same.txt", input);
        }
        using (var input = new MemoryStream(second))
        {
            var result = await _storage.StoreAsync(1, "same.txt", input);
            Assert.Equal(StoreDocumentOutcome.Stored, result.Outcome);
        }

        var path = Path.Combine(_tempRoot, "1", "same.txt");
        Assert.Equal(second, await File.ReadAllBytesAsync(path));
    }

    // ---------- DeleteAllForRequest ----------

    [Fact]
    public async Task DeleteAllForRequestAsync_removes_directory_and_files()
    {
        using (var input = new MemoryStream(new byte[] { 1 }))
        {
            await _storage.StoreAsync(5, "a.txt", input);
        }
        using (var input = new MemoryStream(new byte[] { 2 }))
        {
            await _storage.StoreAsync(5, "b.txt", input);
        }

        await _storage.DeleteAllForRequestAsync(5);

        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "5")));
    }

    [Fact]
    public async Task DeleteAllForRequestAsync_is_idempotent_when_directory_absent()
    {
        // No prior store — directory was never created.
        await _storage.DeleteAllForRequestAsync(123);

        // Calling again is still fine.
        await _storage.DeleteAllForRequestAsync(123);
    }

    // ---------- Filename safety ----------

    [Theory]
    [InlineData("")]
    [InlineData("foo/bar.pdf")]
    [InlineData("foo\\bar.pdf")]
    [InlineData("..\\escape.pdf")]
    [InlineData("../escape.pdf")]
    [InlineData("inner..dotted.pdf")]
    [InlineData("nullbyte\0.pdf")]
    public async Task StoreAsync_throws_InvalidDocumentFileName_for_unsafe_names(string fileName)
    {
        using var input = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidDocumentFileNameException>(
            () => _storage.StoreAsync(1, fileName, input));
    }

    [Fact]
    public async Task StoreAsync_throws_InvalidDocumentFileName_when_length_exceeds_cap()
    {
        // 201 chars total (200 'a's + '.pdf' would be 204; instead make
        // a 201-char base with no extension to land exactly at 201).
        var tooLong = new string('a', 201);
        using var input = new MemoryStream(new byte[] { 1 });
        var ex = await Assert.ThrowsAsync<InvalidDocumentFileNameException>(
            () => _storage.StoreAsync(1, tooLong, input));
        Assert.Contains("exceeds maximum length", ex.Message);
    }

    [Fact]
    public async Task StoreAsync_accepts_filename_at_length_cap()
    {
        // 196 chars + ".txt" = exactly 200.
        var name = new string('a', 196) + ".txt";
        Assert.Equal(200, name.Length);

        using var input = new MemoryStream(new byte[] { 1 });
        var result = await _storage.StoreAsync(1, name, input);
        Assert.Equal(StoreDocumentOutcome.Stored, result.Outcome);
    }

    [Fact]
    public async Task RetrieveAsync_throws_InvalidDocumentFileName_for_unsafe_names()
    {
        await Assert.ThrowsAsync<InvalidDocumentFileNameException>(
            () => _storage.RetrieveAsync(1, "../escape.pdf"));
    }

    // ---------- Extension allow-list ----------

    [Fact]
    public async Task StoreAsync_rejects_disallowed_extension()
    {
        using var input = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await _storage.StoreAsync(1, "evil.exe", input);

        Assert.Equal(StoreDocumentOutcome.RejectedDisallowedExtension, result.Outcome);
        Assert.Equal("exe", result.Extension);
        Assert.Null(result.SizeBytes);
        Assert.False(File.Exists(Path.Combine(_tempRoot, "1", "evil.exe")));
    }

    [Fact]
    public async Task StoreAsync_extension_match_is_case_insensitive()
    {
        using var input = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await _storage.StoreAsync(1, "report.PDF", input);

        Assert.Equal(StoreDocumentOutcome.Stored, result.Outcome);
        Assert.Equal("pdf", result.Extension);
    }

    [Fact]
    public async Task StoreAsync_rejects_filename_without_extension()
    {
        using var input = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await _storage.StoreAsync(1, "noextension", input);

        Assert.Equal(StoreDocumentOutcome.RejectedDisallowedExtension, result.Outcome);
        Assert.Equal(string.Empty, result.Extension);
    }

    // ---------- Size cap ----------

    [Fact]
    public async Task StoreAsync_rejects_file_exceeding_size_cap()
    {
        _settings.Set("Storage.MaxFileSizeBytes", "100");

        using var input = new MemoryStream(new byte[101]);
        var result = await _storage.StoreAsync(1, "big.txt", input);

        Assert.Equal(StoreDocumentOutcome.RejectedFileTooLarge, result.Outcome);
        Assert.Equal(101, result.SizeBytes);
        Assert.Null(result.Extension);
        Assert.False(File.Exists(Path.Combine(_tempRoot, "1", "big.txt")));
    }

    [Fact]
    public async Task StoreAsync_accepts_file_exactly_at_size_cap()
    {
        _settings.Set("Storage.MaxFileSizeBytes", "100");

        using var input = new MemoryStream(new byte[100]);
        var result = await _storage.StoreAsync(1, "atcap.txt", input);

        Assert.Equal(StoreDocumentOutcome.Stored, result.Outcome);
    }

    [Fact]
    public async Task StoreAsync_throws_when_stream_is_not_seekable()
    {
        await using var input = new NonSeekableStream(new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.StoreAsync(1, "ok.txt", input));
    }

    // ---------- Settings re-read on every call ----------

    [Fact]
    public async Task StoreAsync_rereads_settings_each_call()
    {
        // First call accepts pdf.
        using (var input = new MemoryStream(new byte[] { 1 }))
        {
            var first = await _storage.StoreAsync(1, "a.pdf", input);
            Assert.Equal(StoreDocumentOutcome.Stored, first.Outcome);
        }

        // Tighten the allow-list between calls; second call must see it.
        _settings.Set("Storage.AllowedFileExtensions", "txt");

        using (var input = new MemoryStream(new byte[] { 1 }))
        {
            var second = await _storage.StoreAsync(1, "b.pdf", input);
            Assert.Equal(StoreDocumentOutcome.RejectedDisallowedExtension, second.Outcome);
        }
    }

    // ---------- Missing-settings hard failures ----------

    [Fact]
    public async Task StoreAsync_throws_when_BasePath_setting_missing()
    {
        _settings.Set("Storage.BasePath", null);
        using var input = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.StoreAsync(1, "ok.txt", input));
    }

    [Fact]
    public async Task StoreAsync_throws_when_MaxFileSizeBytes_unparseable()
    {
        _settings.Set("Storage.MaxFileSizeBytes", "not-a-number");
        using var input = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.StoreAsync(1, "ok.txt", input));
    }

    // ---------- Helpers ----------

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
