using System;
using System.IO;

namespace RTSP.Tests.TestUtils
{
    public class InOutStream : Stream
    {
        public required Stream In { get; init; }
        public required Stream Out { get; init; }


        public override bool CanRead => In.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => Out.CanWrite;

        public override long Length => throw new NotSupportedException("Simulate NetworkStream");

        public override long Position
        {
            get => throw new NotSupportedException("Simulate NetworkStream");
            set => throw new NotSupportedException("Simulate NetworkStream");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("Simulate NetworkStream");

        public override void SetLength(long value) => throw new NotSupportedException("Simulate NetworkStream");

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return In.Read(buffer, offset, count);
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            return In.BeginRead(buffer, offset, count, callback, state);
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            return In.EndRead(asyncResult);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Out.Write(buffer, offset, count);
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            return Out.BeginWrite(buffer, offset, count, callback, state);
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            Out.EndWrite(asyncResult);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                In.Dispose();
                Out.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
