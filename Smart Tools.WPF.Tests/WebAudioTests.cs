using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>The web-audio command lines and parsers; plus a real run of yt-dlp + ffmpeg against a sound file served from this PC.</summary>
    public class WebAudioTests
    {
        // ---------------------------------------------------------------- the link

        [Theory]
        [InlineData("https://www.youtube.com/watch?v=abc123", "https://www.youtube.com/watch?v=abc123")]
        [InlineData("  <https://example.com/a.mp3>  ", "https://example.com/a.mp3")]
        [InlineData("youtu.be/abc123", "https://youtu.be/abc123")]
        [InlineData("www.example.com/sounds/reveille.ogg", "https://www.example.com/sounds/reveille.ogg")]
        [InlineData("http://example.com/x", "http://example.com/x")]
        public void Pasted_links_are_cleaned(string pasted, string expected)
        {
            Assert.True(WebAudioMath.TryNormalizeUrl(pasted, out string url));
            Assert.Equal(expected, url);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("reveille")]
        [InlineData("--exec calc")]
        [InlineData("-o x")]
        [InlineData("file:///C:/secret.mp3")]
        [InlineData("ftp://example.com/a.mp3")]
        [InlineData("javascript:alert(1)")]
        [InlineData("https://nodot/x")]
        [InlineData("two words.com")]
        public void Things_that_are_not_web_links_are_refused(string pasted) => Assert.False(WebAudioMath.TryNormalizeUrl(pasted, out _));

        // ---------------------------------------------------------------- command lines

        [Fact]
        public void The_link_goes_after_double_dash_so_it_can_never_be_an_option()
        {
            var info = WebAudioMath.InfoArgs("https://example.com/a");
            Assert.Equal("--", info[^2]);
            Assert.Equal("https://example.com/a", info[^1]);
            Assert.Contains("--skip-download", info);

            var args = WebAudioMath.DownloadArgs("https://example.com/a b", @"C:\tmp dir", @"C:\ff mpeg", WebAudioFormat.Mp3, WebAudioQuality.High, null, null);
            Assert.Equal("--", args[^2]);
            Assert.Equal("https://example.com/a b", args[^1]);          // one argument, whatever it holds
            Assert.Contains("--no-playlist", args);                     // one item, never a whole channel
            Assert.Equal("mp3", args[args.ToList().IndexOf("--audio-format") + 1]);
            Assert.Equal("192K", args[args.ToList().IndexOf("--audio-quality") + 1]);
            Assert.Equal(@"C:\ff mpeg", args[args.ToList().IndexOf("--ffmpeg-location") + 1]);
            Assert.StartsWith(@"C:\tmp dir", args[args.ToList().IndexOf("-o") + 1]);
            Assert.DoesNotContain("--download-sections", args);
        }

        [Fact]
        public void Lossless_formats_have_no_bitrate_and_a_clip_range_is_passed_in_seconds()
        {
            var flac = WebAudioMath.DownloadArgs("https://e.com/a", "t", "f", WebAudioFormat.Flac, WebAudioQuality.Best, 12.5, 30);
            Assert.DoesNotContain("--audio-quality", flac);
            Assert.Equal("*12.5-30", flac[flac.ToList().IndexOf("--download-sections") + 1]);
            var open = WebAudioMath.DownloadArgs("https://e.com/a", "t", "f", WebAudioFormat.M4a, WebAudioQuality.Standard, 5, null);
            Assert.Equal("*5-inf", open[open.ToList().IndexOf("--download-sections") + 1]);
            Assert.Equal("128K", open[open.ToList().IndexOf("--audio-quality") + 1]);
        }

        [Theory]
        [InlineData("83", 83.0)]
        [InlineData("1:23", 83.0)]
        [InlineData("0:01:23.5", 83.5)]
        [InlineData("1,5", 1.5)]
        [InlineData("1:00:00", 3600.0)]
        public void Times_are_read_as_a_person_types_them(string text, double seconds)
        {
            Assert.True(WebAudioMath.TryParseTime(text, out double s));
            Assert.Equal(seconds, s, 6);
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("-5")]
        [InlineData("1:2:3:4")]
        public void Bad_times_are_refused(string text) => Assert.False(WebAudioMath.TryParseTime(text, out _));

        [Fact]
        public void Times_are_shown_short()
        {
            Assert.Equal("0:05", WebAudioMath.FormatTime(5));
            Assert.Equal("3:45", WebAudioMath.FormatTime(225));
            Assert.Equal("1:02:03", WebAudioMath.FormatTime(3723));
        }

        // ---------------------------------------------------------------- reading yt-dlp

        [Fact]
        public void The_info_of_a_link_is_read()
        {
            const string json = "{\"title\":\"Reveille (Bugle Call: Awake for Morning Call)\",\"uploader\":\"US Army Band\",\"duration\":35.0,\"is_live\":false,\"extractor_key\":\"Youtube\"}";
            var info = WebAudioMath.ParseInfo(json)!;
            Assert.Equal("Reveille (Bugle Call: Awake for Morning Call)", info.Title);
            Assert.Equal("US Army Band", info.Uploader);
            Assert.Equal(35.0, info.DurationSeconds);
            Assert.False(info.IsLive);
            Assert.Equal("Youtube", info.Site);
            Assert.Equal("Some Channel", WebAudioMath.ParseInfo("{\"title\":\"x\",\"channel\":\"Some Channel\"}")!.Uploader);
            Assert.True(WebAudioMath.ParseInfo("{\"title\":\"x\",\"is_live\":true}")!.IsLive);
            Assert.Null(WebAudioMath.ParseInfo("{}"));
            Assert.Null(WebAudioMath.ParseInfo("ERROR: Unsupported URL"));
        }

        [Fact]
        public void Progress_lines_give_percent_speed_and_time_left()
        {
            Assert.Equal(45.3, WebAudioMath.ParseProgress("PROGRESS| 45.3%|1.2MiB/s|00:12"));
            Assert.Equal(100, WebAudioMath.ParseProgress("PROGRESS|100.0%|NA|00:00"));
            Assert.Null(WebAudioMath.ParseProgress("[ExtractAudio] Destination: a.mp3"));
            var (speed, eta) = WebAudioMath.ParseSpeedEta("PROGRESS| 45.3%|1.2MiB/s|00:12");
            Assert.Equal("1.2MiB/s", speed);
            Assert.Equal("00:12", eta);
            Assert.Equal(("", ""), WebAudioMath.ParseSpeedEta("PROGRESS| 10.0%|NA|NA"));
        }

        [Theory]
        [InlineData("ERROR: [generic] Unsupported URL: https://x.com/y", WebAudioError.Unsupported)]
        [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access", WebAudioError.Private)]
        [InlineData("ERROR: [youtube] abc: Sign in to confirm you're not a bot", WebAudioError.SignIn)]
        [InlineData("ERROR: [youtube] abc: Video unavailable", WebAudioError.Unavailable)]
        [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", WebAudioError.Blocked)]
        [InlineData("ERROR: Unable to download webpage: getaddrinfo failed", WebAudioError.Network)]
        [InlineData("ERROR: Postprocessing: ffmpeg not found", WebAudioError.NoFfmpeg)]
        [InlineData("something odd", WebAudioError.Other)]
        public void Complaints_are_put_in_plain_categories(string text, WebAudioError expected) => Assert.Equal(expected, WebAudioMath.Classify(text));

        [Fact]
        public void The_last_error_line_is_pulled_out() =>
            Assert.Equal("second", WebAudioMath.LastError("noise\r\nERROR: first\r\nmore\r\nERROR: second\r\n"));

        // ---------------------------------------------------------------- the real programs

        private static string? FindEngines()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string engines = Path.Combine(dir.FullName, "Smart Tools.WPF", "Engines");
                if (File.Exists(Path.Combine(engines, "YTDLP", "yt-dlp.exe")) && File.Exists(Path.Combine(engines, "FFMPEG", "ffmpeg.exe"))) return engines;
            }
            return null;
        }

        private static byte[] SineWav(double seconds = 1.0, int rate = 22050)
        {
            int n = (int)(rate * seconds);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.ASCII);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000));
            return ms.ToArray();
        }

        [Fact]
        public void A_sound_file_on_a_web_address_is_downloaded_and_converted_with_the_real_programs()
        {
            string? engines = FindEngines();
            if (engines == null) return;   // the programs are not beside the sources

            // a tiny web server on this PC, so no real site is touched
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            byte[] wav = SineWav();
            var serve = Task.Run(() =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var ctx = listener.GetContext();
                        ctx.Response.ContentType = "audio/wav";
                        ctx.Response.ContentLength64 = wav.Length;
                        if (ctx.Request.HttpMethod != "HEAD") ctx.Response.OutputStream.Write(wav);
                        ctx.Response.Close();
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    // stopped
                }
            });

            string temp = Path.Combine(Path.GetTempPath(), "st-webaudio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                Assert.True(WebAudioMath.TryNormalizeUrl($"http://127.0.0.1:{port}/Reveille test.wav".Replace(" ", "%20"), out string url));
                string exe = Path.Combine(engines, "YTDLP", "yt-dlp.exe");

                var info = Run(exe, WebAudioMath.InfoArgs(url));
                var parsed = WebAudioMath.ParseInfo(info.Output);
                Assert.True(parsed != null, info.Output);

                var args = WebAudioMath.DownloadArgs(url, temp, Path.Combine(engines, "FFMPEG"), WebAudioFormat.Mp3, WebAudioQuality.Standard, null, null);
                var run = Run(exe, args);
                Assert.True(run.Code == 0, run.Output);
                var made = Directory.GetFiles(temp, "*.mp3");
                Assert.Single(made);
                Assert.True(new FileInfo(made[0]).Length > 1000);
            }
            finally
            {
                listener.Stop();
                serve.Wait(2000);
                try
                {
                    Directory.Delete(temp, true);
                }
                catch (IOException)
                {
                }
            }
        }

        private static (int Code, string Output) Run(string exe, IReadOnlyList<string> args)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(90_000)) p.Kill(true);
            return (p.ExitCode, output + err.Result);
        }
    }
}
