using System.Text;
using Synthapixel.MonoGame.AgentTools.Input;

namespace Synthapixel.MonoGame.AgentTools.Tests;

public class StreamedFileTests
{
    /// <summary>Keeps what was written after the file disposes it.</summary>
    private sealed class KeptStream : MemoryStream
    {
        public string Text { get; private set; } = "";

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            Text = Encoding.UTF8.GetString(ToArray());
        }
    }

    private sealed class FullDisk : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("There is not enough space on the disk.");
    }

    private static void Record(StreamedFile file, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        bytes.CopyTo(file.GetSpan(bytes.Length));
        file.Advance(bytes.Length);
    }

    [Fact]
    public void Writes_every_record_on_dispose()
    {
        var stream = new KeptStream();
        using (var file = new StreamedFile(stream, bufferSize: 16))
        {
            for (int i = 0; i < 10; ++i)
                Record(file, $"line {i}\n");
        }

        Assert.Equal(string.Concat(Enumerable.Range(0, 10).Select(i => $"line {i}\n")), stream.Text);
    }

    [Fact]
    public async Task A_failed_write_is_reported_without_blocking_flush_or_dispose()
    {
        var file = new StreamedFile(new FullDisk(), bufferSize: 16);
        Record(file, "0123456789\n");
        Record(file, "0123456789\n");   // fills the buffer: the first goes to the worker, which fails

        // Times out (and fails) if a flush waits on a worker that died.
        var cancel = TestContext.Current.CancellationToken;
        await Task.Run(() =>
        {
            file.Flush();
            file.FlushFinal();
            file.Dispose();
        }, cancel).WaitAsync(TimeSpan.FromSeconds(10), cancel);

        Assert.Contains("not enough space", file.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlushFinal_writes_committed_records_and_nothing_after()
    {
        var stream = new KeptStream();
        var file = new StreamedFile(stream);
        Record(file, "kept\n");
        var partial = Encoding.UTF8.GetBytes("torn");
        partial.CopyTo(file.GetSpan(partial.Length));   // being written when the crash flush runs

        await Task.Run(file.FlushFinal, TestContext.Current.CancellationToken);
        file.Advance(partial.Length);
        Record(file, "after\n");
        file.Dispose();

        Assert.Equal("kept\n", stream.Text);
    }
}
