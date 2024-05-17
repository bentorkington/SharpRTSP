using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Rtsp.Rtp.Tests
{
    [TestFixture()]
    public class RawMediaFrameTests
    {
        [Test()]
        public void AnyTest()
        {
            List<ReadOnlyMemory<byte>> data = [
                new byte[] {0x01 }.AsMemory(),
              ];
            RawMediaFrame rawMediaFrame = new(data, []) { ClockTimestamp = DateTime.MinValue, RtpTimestamp = 0 };
            Assert.That(rawMediaFrame.Any(), Is.True);
        }

        [Test()]
        public void AnyEmptyTest()
        {
            RawMediaFrame rawMediaFrame = RawMediaFrame.Empty;
            Assert.That(rawMediaFrame.Any(), Is.False);
        }

        [Test()]
        public void AnyDisposedTest()
        {
            {
                // Validate that Empty RawMediaFrame do not throw ObjectDisposedException when used multiple times
                using RawMediaFrame rawMediaFrame = RawMediaFrame.Empty;
                Assert.That(rawMediaFrame.Any(), Is.False);
            }
            {
                using RawMediaFrame rawMediaFrame = RawMediaFrame.Empty;
                Assert.That(rawMediaFrame.Any(), Is.False);
            }
        }
    }
}