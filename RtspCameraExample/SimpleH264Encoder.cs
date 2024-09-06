using System;
using System.Linq;

namespace RtspCameraExample
{
    // Simple H264 Encoder
    // Written by Jordi Cenzano (www.jordicenzano.name)
    //
    // Ported to C# by Roger Hardiman www.rjh.org.uk

    // This is a very simple lossless H264 encoder. No compression is used and so the output NAL data is as
    // large as the input YUV data.
    // It is used for a quick example of H264 encoding in pure .Net without needing OS specific APIs
    // or cross compiled C libraries.
    //
    // SimpleH264Encoder can use any image Width or Height
    public class SimpleH264Encoder
    {
        private readonly CJOCh264encoder h264encoder = new();

        // Constuctor
        public SimpleH264Encoder(int width, int height)
        {
            // Initialise H264 encoder.
            h264encoder.IniCoder(width, height, CJOCh264encoder.SampleFormat.SAMPLE_FORMAT_YUV420p);
            // NAL array will contain SPS and PPS

        }

        // Raw SPS with no Size Header and no 00 00 00 01 headers
        public byte[] GetRawSPS() => h264encoder?.sps?.Skip(4).ToArray() ?? [];

        public byte[] GetRawPPS() => h264encoder?.pps?.Skip(4).ToArray() ?? [];

        public ReadOnlySpan<byte> CompressFrame(ReadOnlySpan<byte> yuv_data)
        {
            // Get the NAL (which has the 00 00 00 01 header)
            var nal_with_header = h264encoder.CodeAndSaveFrame(yuv_data);

            return nal_with_header[4..];
        }
    }
}