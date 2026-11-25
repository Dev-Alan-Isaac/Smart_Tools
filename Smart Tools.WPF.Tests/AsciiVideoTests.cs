using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>ASCII video: grid size, brightness to characters, stamping characters into frames, the text file, and a real FFmpeg round trip.</summary>
    public class AsciiVideoTests
    {
        [Fact]
        public void Rows_keep_the_picture_shape_for_tall_character_cells()
        {
            Assert.Equal(34, AsciiVideoMath.RowsFor(120, 1440, 1080, 0.375));   // Bad Apple is 4:3: 120 * 0.75 * 0.375 = 33.75
            Assert.Equal(60, AsciiVideoMath.RowsFor(120, 1, 1, 0.5));
            Assert.Equal(60, AsciiVideoMath.RowsFor(120, 0, 0, 0.5));            // unknown size: a sensible guess, not a crash
        }

        [Fact]
        public void The_layout_is_about_the_asked_width_with_even_sides()
        {
            var layout = AsciiVideoMath.Layout(120, 1440, 1080, 0.5);
            Assert.Equal(120, layout.Columns);
            Assert.Equal(12, layout.CellWidth);
            Assert.Equal(24, layout.CellHeight);
            Assert.Equal(1440, layout.Width);
            Assert.Equal(0, layout.Width % 2);
            Assert.Equal(0, layout.Height % 2);
            Assert.Equal(layout.Columns * layout.Rows, layout.CellBytes);

            var odd = AsciiVideoMath.Layout(81, 1920, 1080, 0.55);
            Assert.Equal(0, odd.Width % 2);
            Assert.Equal(0, odd.Height % 2);
            Assert.Equal(AsciiVideoMath.MaxColumns, AsciiVideoMath.Layout(9999, 100, 100, 0.5).Columns);
            Assert.Equal(AsciiVideoMath.MinColumns, AsciiVideoMath.Layout(1, 100, 100, 0.5).Columns);
        }

        [Fact]
        public void Bright_is_dense_on_black_and_dark_is_dense_on_white()
        {
            string ramp = AsciiVideoMath.RampText(AsciiRamp.Smooth);
            byte[] grey = { 0, 128, 255 };
            Assert.Equal(new[] { ' ', ramp[5], '@' }, AsciiVideoMath.FrameToChars(grey, ramp, whiteOnBlack: true));
            Assert.Equal(new[] { '@', ramp[4], ' ' }, AsciiVideoMath.FrameToChars(grey, ramp, whiteOnBlack: false));
            Assert.Equal(new[] { ' ', '#', '#' }, AsciiVideoMath.FrameToChars(grey, AsciiVideoMath.RampText(AsciiRamp.Sharp), true));
        }

        [Fact]
        public void Every_ramp_starts_with_a_space_and_has_no_repeats()
        {
            foreach (AsciiRamp ramp in Enum.GetValues<AsciiRamp>())
            {
                string text = AsciiVideoMath.RampText(ramp);
                Assert.Equal(' ', text[0]);
                Assert.Equal(text.Length, text.Distinct().Count());
            }
        }

        [Fact]
        public void Characters_become_lines_of_equal_width()
        {
            string lines = AsciiVideoMath.ToLines("ab  cd".ToCharArray(), 2);
            Assert.Equal("ab\n  \ncd\n", lines);
        }

        [Fact]
        public void Characters_are_stamped_into_their_cells_and_spaces_leave_the_background()
        {
            var layout = new AsciiLayout(2, 1, 2, 2);                       // two cells of 2 x 2 pixels: a 4 x 2 picture
            var masks = new Dictionary<char, byte[]> { ['#'] = new byte[] { 255, 0, 0, 200 } };
            var picture = new byte[layout.Width * layout.Height];

            AsciiVideoMath.Compose(new[] { '#', ' ' }, layout, masks, whiteOnBlack: true, picture);
            Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 200, 0, 0 }, picture);

            AsciiVideoMath.Compose(new[] { ' ', '#' }, layout, masks, whiteOnBlack: false, picture);
            Assert.Equal(new byte[] { 255, 255, 0, 255, 255, 255, 255, 55 }, picture);
        }

        [Fact]
        public void The_text_file_header_is_written_and_read_back()
        {
            string header = AsciiVideoMath.Header(120, 34, 30, 6572);
            Assert.Equal("ASCIIVIDEO 1 120 34 30 6572", header);
            Assert.True(AsciiVideoMath.TryReadHeader(header, out int c, out int r, out int f, out int n));
            Assert.Equal((120, 34, 30, 6572), (c, r, f, n));
            Assert.False(AsciiVideoMath.TryReadHeader("hello", out _, out _, out _, out _));
            Assert.False(AsciiVideoMath.TryReadHeader("ASCIIVIDEO 1 0 34 30 5", out _, out _, out _, out _));
        }

        [Fact]
        public void The_player_reads_the_same_header_and_the_launcher_calls_it()
        {
            string script = AsciiVideoMath.PlayerScript();
            Assert.Contains("frames.txt", script);
            Assert.Contains("sound.wav", script);
            Assert.Contains("'ASCIIVIDEO'", script);
            Assert.Contains("Escape", script);
            Assert.Contains("play.ps1", AsciiVideoMath.PlayerCmd());
        }

        [Fact]
        public void The_probe_answer_is_read()
        {
            const string json = "{\"streams\":[{\"codec_type\":\"video\",\"width\":480,\"height\":360},{\"codec_type\":\"audio\"}],\"format\":{\"duration\":\"219.011\"}}";
            var probe = AsciiVideoMath.ParseProbe(json)!;
            Assert.Equal(480, probe.Width);
            Assert.Equal(360, probe.Height);
            Assert.Equal(219.011, probe.DurationSeconds, 3);
            Assert.True(probe.HasAudio);
            Assert.Null(AsciiVideoMath.ParseProbe("{\"streams\":[{\"codec_type\":\"audio\"}]}"));   // sound only: no video
            Assert.Null(AsciiVideoMath.ParseProbe("nonsense"));
        }

        [Fact]
        public void The_command_lines_put_the_range_and_sizes_where_ffmpeg_expects_them()
        {
            var layout = new AsciiLayout(120, 34, 12, 24);
            var decode = AsciiVideoMath.DecodeArgs(@"C:\in put\bad apple.mp4", 10, 30, 30, layout);
            Assert.True(decode.ToList().IndexOf("-ss") < decode.ToList().IndexOf("-i"));   // fast seek before the input
            Assert.Contains("fps=30,scale=120:34:flags=area,format=gray", decode);
            Assert.Equal("pipe:1", decode[^1]);

            var encode = AsciiVideoMath.EncodeArgs(@"C:\in.mp4", null, null, 30, layout, sound: true, @"C:\out.mp4");
            Assert.Contains("1440x816", encode);
            Assert.Contains("1:a:0?", encode);                                              // "?": a video without sound still works
            Assert.Equal(@"C:\out.mp4", encode[^1]);
            Assert.DoesNotContain("-map", AsciiVideoMath.EncodeArgs("in", null, null, 30, layout, sound: false, "out.mp4"));

            var download = AsciiVideoMath.DownloadArgs("https://example.com/v", @"C:\t", @"C:\ff");
            Assert.Equal("--", download[^2]);
            Assert.Contains("--no-playlist", download);
        }

        // ---------------------------------------------------------------- the real FFmpeg

        private static string? FindFfmpeg()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string exe = Path.Combine(dir.FullName, "Smart Tools.WPF", "Engines", "FFMPEG", "ffmpeg.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        [Fact]
        public void A_test_video_is_decoded_to_character_cells_and_drawn_frames_are_encoded_back()
        {
            string? ffmpeg = FindFfmpeg();
            if (ffmpeg == null) return;
            string dir = Path.Combine(Path.GetTempPath(), "st-asciivideo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // a 2-second test pattern with a tone, made by FFmpeg itself
                string source = Path.Combine(dir, "source.mp4");
                Run(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30", "-f", "lavfi", "-i", "sine=frequency=440",
                    "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, null, out _);

                var layout = AsciiVideoMath.Layout(80, 320, 240, 0.5, targetWidth: 640);
                var decoded = new MemoryStream();
                Assert.Equal(0, Run(ffmpeg, AsciiVideoMath.DecodeArgs(source, null, null, 15, layout).ToArray(), null, out byte[] raw));
                int frames = raw.Length / layout.CellBytes;
                Assert.Equal(0, raw.Length % layout.CellBytes);
                Assert.InRange(frames, 29, 31);                                   // 2 s at 15 fps

                // draw every frame with a plain block "font" and encode with the original sound
                var masks = new Dictionary<char, byte[]>();
                foreach (char ch in AsciiVideoMath.RampText(AsciiRamp.Smooth))
                    masks[ch] = Enumerable.Repeat((byte)(ch == ' ' ? 0 : 200), layout.CellWidth * layout.CellHeight).ToArray();
                var drawn = new MemoryStream();
                var picture = new byte[layout.Width * layout.Height];
                for (int f = 0; f < frames; f++)
                {
                    var chars = AsciiVideoMath.FrameToChars(raw.AsSpan(f * layout.CellBytes, layout.CellBytes), AsciiVideoMath.RampText(AsciiRamp.Smooth), true);
                    AsciiVideoMath.Compose(chars, layout, masks, true, picture);
                    drawn.Write(picture);
                }
                string output = Path.Combine(dir, "ascii.mp4");
                Assert.Equal(0, Run(ffmpeg, AsciiVideoMath.EncodeArgs(source, null, null, 15, layout, true, output).ToArray(), drawn.ToArray(), out _));
                Assert.True(new FileInfo(output).Length > 1000);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        private static int Run(string exe, string[] args, byte[]? input, out byte[] output)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (string a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            var err = p.StandardError.ReadToEndAsync();
            var outBuffer = new MemoryStream();
            var reader = p.StandardOutput.BaseStream.CopyToAsync(outBuffer);
            if (input != null) p.StandardInput.BaseStream.Write(input);
            p.StandardInput.Close();
            reader.Wait();
            p.WaitForExit();
            output = outBuffer.ToArray();
            Assert.True(p.ExitCode == 0, err.Result);
            return p.ExitCode;
        }
    }
}
