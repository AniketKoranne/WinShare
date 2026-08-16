using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// ─────────────────────────────────────────────────────────────────────────────
//  WinShare  –  WiFi File Transfer  (Send + Receive, offline QR, modern UI)
//  Drop-in replacement for the original WinShare project.
//
//  Dependencies (all inbox / NuGet):
//    • QRCoder  → Install-Package QRCoder
//    • Nothing else – all file handling is pure BCL
//
//  HOW IT WORKS
//    GET  /           → Upload page  (phone → PC)
//    GET  /files      → Download page listing shared files  (PC → phone)
//    GET  /dl?f=name  → Actual file download stream
//    POST /           → Receive multipart file upload from phone
// ─────────────────────────────────────────────────────────────────────────────

namespace WinShare
{
    public partial class MainWindow : Window
    {
        // ── state ────────────────────────────────────────────────────────────
        private HttpListener?      _listener;
        private string             _localIp   = "127.0.0.1";
        private string             _port      = "8080";
        private bool               _running   = false;

        private readonly List<string> _sharedFiles = new();   // files to send
        private int _receivedCount = 0;
        private int _sentCount     = 0;

        // ── init ─────────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            _localIp = DetectWifiIP();
            UpdateSavePathLabel();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IP DETECTION
        // ═════════════════════════════════════════════════════════════════════
        private static string DetectWifiIP()
        {
            // Prefer active Wireless / Wi-Fi interfaces
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                bool isWifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                           || ni.Description.ToLowerInvariant().Contains("wireless")
                           || ni.Description.ToLowerInvariant().Contains("wi-fi")
                           || ni.Description.ToLowerInvariant().Contains("wlan");

                if (!isWifi) continue;

                foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                        return ip.Address.ToString();
            }

            // Fallback – any non-loopback IPv4
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        return ip.ToString();
            }
            catch { /* ignore */ }

            return "127.0.0.1";
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SERVER START / STOP
        // ═════════════════════════════════════════════════════════════════════
        private void StartServer_Click(object sender, RoutedEventArgs e)
        {
            _port = PortBox.Text.Trim();
            if (!int.TryParse(_port, out int p) || p < 1024 || p > 65535)
            {
                MessageBox.Show("Enter a valid port number (1024 – 65535).", "Invalid Port",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Re-detect IP each start (user may have switched networks)
            _localIp = DetectWifiIP();

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://{_localIp}:{_port}/");
                _listener.Start();
                _running = true;

                Task.Run(ListenLoop);

                // UI
                string url = $"http://{_localIp}:{_port}";
                IpLabel.Text        = url;
                StatusLabel.Text    = "Running";
                StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                StatusDot.Fill      = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                StartBtn.IsEnabled  = false;
                StopBtn.IsEnabled   = true;
                CopyUrlBtn.IsEnabled  = true;
                AddFilesBtn.IsEnabled = true;
                PortBox.IsEnabled   = false;

                ShowQrCode(url);
                AddLog($"✅ Server started on {url}", "#10B981");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not start server:\n{ex.Message}\n\n" +
                                "Try a different port, or run as Administrator.",
                                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopServer_Click(object sender, RoutedEventArgs e)
        {
            StopServer();
        }

        private void StopServer()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;

            Dispatcher.Invoke(() =>
            {
                StatusLabel.Text    = "Stopped";
                StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                StatusDot.Fill      = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                IpLabel.Text        = "—";
                StartBtn.IsEnabled  = true;
                StopBtn.IsEnabled   = false;
                CopyUrlBtn.IsEnabled  = false;
                AddFilesBtn.IsEnabled = false;
                PortBox.IsEnabled   = true;
                QrCodeImage.Source  = null;
                QrPlaceholder.Visibility = Visibility.Visible;
                AddLog("⏹ Server stopped.", "#94A3B8");
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        //  HTTP LISTENER LOOP
        // ═════════════════════════════════════════════════════════════════════
        private async Task ListenLoop()
        {
            while (_running && _listener != null && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(ctx));
                }
                catch (HttpListenerException) { /* normal on Stop() */ }
                catch (ObjectDisposedException) { /* normal on Stop() */ }
                catch { /* swallow unexpected */ }
            }
        }

        private async void HandleRequest(HttpListenerContext ctx)
        {
            var req  = ctx.Request;
            var resp = ctx.Response;

            try
            {
                string path = req.Url?.AbsolutePath ?? "/";

                if (req.HttpMethod == "GET" && path == "/")
                {
                    await WriteHtml(resp, BuildUploadPage());
                }
                else if (req.HttpMethod == "GET" && path == "/files")
                {
                    await WriteHtml(resp, BuildDownloadPage());
                }
                else if (req.HttpMethod == "GET" && path == "/dl")
                {
                    await ServeFileDownload(req, resp);
                }
                else if (req.HttpMethod == "POST" && path == "/")
                {
                    ReceiveUpload(req);
                    await WriteHtml(resp, BuildSuccessPage());
                }
                else
                {
                    resp.StatusCode = 404;
                    await WriteHtml(resp, "<html><body><h2>404 – Not Found</h2></body></html>");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    resp.StatusCode = 500;
                    await WriteHtml(resp, $"<html><body><h2>Server Error</h2><pre>{ex.Message}</pre></body></html>");
                }
                catch { }
            }
            finally
            {
                try { resp.Close(); } catch { }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  RECEIVE FILE FROM PHONE  (multipart/form-data)
        // ═════════════════════════════════════════════════════════════════════
        private void ReceiveUpload(HttpListenerRequest request)
        {
            try
            {
                string ct = request.ContentType ?? "";
                if (!ct.Contains("boundary="))
                    throw new Exception("Not a multipart upload.");

                string boundary = ct.Split(new[] { "boundary=" }, StringSplitOptions.None)[1]
                                    .Split(';')[0].Trim();

                using var ms = new MemoryStream();
                request.InputStream.CopyTo(ms);
                byte[] raw = ms.ToArray();

                // ── extract filename from Content-Disposition header ─────────
                string headerSnip = Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, 2048));
                string filename = "upload_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dat";

                const string fnKey = "filename=\"";
                int fi = headerSnip.IndexOf(fnKey, StringComparison.Ordinal);
                if (fi != -1)
                {
                    int fs = fi + fnKey.Length;
                    int fe = headerSnip.IndexOf('"', fs);
                    if (fe != -1)
                        filename = Path.GetFileName(headerSnip.Substring(fs, fe - fs));
                }

                // ── find where the actual file bytes begin ───────────────────
                // After the FIRST \r\n\r\n following the boundary header block
                byte[] needle = { 13, 10, 13, 10 };
                int dataStart = FindBytes(raw, needle);
                if (dataStart == -1) throw new Exception("Malformed multipart body.");
                dataStart += needle.Length;

                // ── strip trailing boundary  --boundary--\r\n ────────────────
                byte[] tailMarker = Encoding.UTF8.GetBytes("\r\n--" + boundary);
                int tailPos = FindBytes(raw, tailMarker, dataStart);
                int dataLen = (tailPos == -1)
                    ? raw.Length - dataStart
                    : tailPos - dataStart;

                if (dataLen <= 0) throw new Exception("Empty file payload.");

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string dest    = UniqueFilePath(desktop, filename);

                using (var fs2 = File.Create(dest))
                    fs2.Write(raw, dataStart, dataLen);

                long kb = (dataLen + 512) / 1024;
                _receivedCount++;

                Dispatcher.Invoke(() =>
                {
                    AddLog($"📥 Received: {Path.GetFileName(dest)}  ({kb} KB)", "#3B82F6");
                    ReceivedCount.Text = $"{_receivedCount} file{(_receivedCount == 1 ? "" : "s")}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AddLog($"❌ Upload error: {ex.Message}", "#EF4444"));
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SERVE FILE TO PHONE  (download)
        // ═════════════════════════════════════════════════════════════════════
        private async Task ServeFileDownload(HttpListenerRequest req, HttpListenerResponse resp)
        {
            string? fname = req.QueryString["f"];
            if (string.IsNullOrEmpty(fname)) { resp.StatusCode = 400; return; }

            string? fullPath = _sharedFiles.Find(p =>
                Path.GetFileName(p).Equals(fname, StringComparison.OrdinalIgnoreCase));

            if (fullPath == null || !File.Exists(fullPath))
            {
                resp.StatusCode = 404;
                await WriteHtml(resp, "<html><body><h2>File not found.</h2></body></html>");
                return;
            }

            resp.ContentType = "application/octet-stream";
            resp.AddHeader("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(fullPath)}\"");
            resp.ContentLength64 = new FileInfo(fullPath).Length;

            using var fs = File.OpenRead(fullPath);
            await fs.CopyToAsync(resp.OutputStream);

            long kb = (new FileInfo(fullPath).Length + 512) / 1024;
            _sentCount++;

            Dispatcher.Invoke(() =>
            {
                AddLog($"📤 Sent: {Path.GetFileName(fullPath)}  ({kb} KB)", "#10B981");
                SentCount.Text = $"{_sentCount} file{(_sentCount == 1 ? "" : "s")}";
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        //  HTML PAGES (served to the phone browser)
        // ═════════════════════════════════════════════════════════════════════
        private static string HtmlShell(string title, string body) => $@"<!DOCTYPE html>
<html lang='en'>
<head>
  <meta charset='UTF-8'/>
  <meta name='viewport' content='width=device-width,initial-scale=1.0,maximum-scale=1.0'/>
  <title>{title}</title>
  <style>
    *{{box-sizing:border-box;margin:0;padding:0}}
    body{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;
          background:#F0F4F8;color:#1E293B;min-height:100vh}}
    .top{{background:linear-gradient(135deg,#3B82F6,#1D4ED8);
           color:#fff;padding:18px 20px;display:flex;align-items:center;gap:12px}}
    .top h1{{font-size:1.25rem;font-weight:700}}
    .top span{{font-size:1.6rem}}
    nav{{display:flex;background:#fff;border-bottom:1px solid #E2E8F0}}
    nav a{{flex:1;padding:12px;text-align:center;text-decoration:none;color:#64748B;
            font-size:.9rem;font-weight:600;border-bottom:3px solid transparent}}
    nav a.active{{color:#3B82F6;border-color:#3B82F6}}
    .content{{padding:20px;max-width:480px;margin:auto}}
    .card{{background:#fff;border-radius:14px;padding:20px;
            box-shadow:0 2px 8px rgba(0,0,0,.07);margin-bottom:16px}}
    .card h2{{font-size:1rem;font-weight:700;margin-bottom:14px;color:#1E293B}}
    .btn{{display:block;width:100%;padding:14px;border:none;border-radius:10px;
           font-size:1rem;font-weight:700;cursor:pointer;transition:.15s}}
    .btn-blue{{background:#3B82F6;color:#fff}}
    .btn-green{{background:#10B981;color:#fff}}
    .btn:active{{opacity:.8}}
    .file-item{{display:flex;align-items:center;justify-content:space-between;
                 padding:12px;border-radius:8px;background:#F8FAFC;
                 border:1px solid #E2E8F0;margin-bottom:10px}}
    .file-name{{font-size:.9rem;color:#1E293B;word-break:break-all;flex:1;margin-right:10px}}
    .dl-btn{{padding:8px 16px;background:#3B82F6;color:#fff;border:none;
              border-radius:8px;font-weight:700;font-size:.85rem;cursor:pointer;white-space:nowrap}}
    .empty{{text-align:center;color:#94A3B8;padding:30px;font-size:.95rem}}
    #drop-zone{{border:2px dashed #CBD5E1;border-radius:12px;padding:30px 20px;
                 text-align:center;transition:.2s;margin-bottom:16px;background:#F8FAFC}}
    #drop-zone.over{{border-color:#3B82F6;background:#EFF6FF}}
    #drop-zone p{{color:#64748B;font-size:.9rem;margin-top:8px}}
    #drop-zone span{{font-size:2.5rem}}
    #file-input{{display:none}}
    #preview{{margin-bottom:12px;font-size:.9rem;color:#3B82F6;font-weight:600;min-height:20px}}
    #progress-wrap{{display:none;margin-bottom:12px}}
    #progress-bar{{height:6px;background:#E2E8F0;border-radius:3px;overflow:hidden}}
    #progress-fill{{height:100%;background:#3B82F6;width:0%;transition:.2s}}
    #progress-text{{font-size:.8rem;color:#64748B;margin-top:4px}}
    .success{{background:#ECFDF5;border:1px solid #6EE7B7;color:#065F46;
               border-radius:10px;padding:14px;text-align:center;font-weight:600}}
    .badge{{display:inline-block;background:#EFF6FF;color:#3B82F6;
             font-size:.75rem;font-weight:700;padding:2px 8px;border-radius:20px;margin-left:6px}}
  </style>
</head>
<body>
  <div class='top'><span>📡</span><h1>WinShare</h1></div>
  <nav>
    <a href='/' {(title == "Upload" ? "class='active'" : "")}>📥 Upload</a>
    <a href='/files' {(title == "Files" ? "class='active'" : "")}>📤 Download</a>
  </nav>
  <div class='content'>{body}</div>
  <script>
    // Tap nav to switch tabs
    document.querySelectorAll('nav a').forEach(a=>a.addEventListener('click',e=>{{
      document.querySelectorAll('nav a').forEach(x=>x.classList.remove('active'));
      e.currentTarget.classList.add('active');
    }}));
  </script>
</body>
</html>";

        private string BuildUploadPage() => HtmlShell("Upload", @"
<div class='card'>
  <h2>Send a file to PC</h2>
  <div id='drop-zone' onclick='document.getElementById(""file-input"").click()'>
    <span>📂</span>
    <p>Tap to select file<br/><small>or drag &amp; drop</small></p>
  </div>
  <input type='file' id='file-input' multiple onchange='onFilePicked(this)'/>
  <div id='preview'></div>
  <div id='progress-wrap'>
    <div id='progress-bar'><div id='progress-fill'></div></div>
    <div id='progress-text'>Uploading…</div>
  </div>
  <button class='btn btn-blue' onclick='doUpload()'>⬆ Upload to PC</button>
</div>
<script>
var files=[];
function onFilePicked(inp){
  files=Array.from(inp.files);
  document.getElementById('preview').textContent=
    files.map(f=>f.name+' ('+Math.round(f.size/1024)+' KB)').join(', ');
}
var dz=document.getElementById('drop-zone');
dz.addEventListener('dragover',e=>{e.preventDefault();dz.classList.add('over')});
dz.addEventListener('dragleave',()=>dz.classList.remove('over'));
dz.addEventListener('drop',e=>{
  e.preventDefault();dz.classList.remove('over');
  files=Array.from(e.dataTransfer.files);
  document.getElementById('preview').textContent=
    files.map(f=>f.name+' ('+Math.round(f.size/1024)+' KB)').join(', ');
});
async function doUpload(){
  if(!files.length){alert('Please select a file first.');return;}
  var pw=document.getElementById('progress-wrap');
  var pf=document.getElementById('progress-fill');
  var pt=document.getElementById('progress-text');
  pw.style.display='block';
  for(var i=0;i<files.length;i++){
    var fd=new FormData();
    fd.append('file',files[i],files[i].name);
    pt.textContent='Uploading '+files[i].name+'…';
    await new Promise((res,rej)=>{
      var xhr=new XMLHttpRequest();
      xhr.open('POST','/');
      xhr.upload.onprogress=e=>{
        if(e.lengthComputable){
          var pct=Math.round(e.loaded/e.total*100);
          pf.style.width=pct+'%';
          pt.textContent='Uploading '+files[i].name+' – '+pct+'%';
        }
      };
      xhr.onload=()=>res();
      xhr.onerror=()=>rej(new Error('Network error'));
      xhr.send(fd);
    });
  }
  pw.style.display='none';
  pf.style.width='0%';
  document.querySelector('.card').innerHTML=""<div class='success'>✅ All files uploaded!</div><br/><button class='btn btn-blue' onclick='location.reload()'>Upload more</button>"";
}
</script>");

        private string BuildDownloadPage()
        {
            var sb = new StringBuilder();
            sb.Append("<div class='card'><h2>Files from PC</h2>");

            if (_sharedFiles.Count == 0)
            {
                sb.Append("<div class='empty'>No files shared yet.<br/>Add files in the WinShare app.</div>");
            }
            else
            {
                foreach (string fp in _sharedFiles)
                {
                    if (!File.Exists(fp)) continue;
                    var info = new FileInfo(fp);
                    string name = info.Name;
                    string size = FormatSize(info.Length);
                    string enc  = Uri.EscapeDataString(name);
                    sb.Append($"<div class='file-item'>" +
                              $"<span class='file-name'>{HtmlEncode(name)}" +
                              $"<span class='badge'>{size}</span></span>" +
                              $"<a href='/dl?f={enc}'>" +
                              $"<button class='dl-btn'>⬇ Get</button></a></div>");
                }
            }

            sb.Append("</div><script>" +
                      "setInterval(()=>location.reload(),10000);" + // auto-refresh every 10s
                      "</script>");
            return HtmlShell("Files", sb.ToString());
        }

        private static string BuildSuccessPage() => HtmlShell("Upload", @"
<div class='card'>
  <div class='success'>✅ File uploaded successfully!</div>
  <br/>
  <a href='/'><button class='btn btn-blue'>Upload another</button></a>
  <br/>
  <a href='/files'><button class='btn btn-green' style='margin-top:10px'>⬇ Download files</button></a>
</div>");

        // ═════════════════════════════════════════════════════════════════════
        //  QR CODE  (pure C# – offline, no external service)
        // ═════════════════════════════════════════════════════════════════════
        private void ShowQrCode(string url)
        {
            try
            {
                // Use QRCoder NuGet package
                using var qrGenerator = new QRCoder.QRCodeGenerator();
                var qrData   = qrGenerator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
                var qrCode   = new QRCoder.QRCode(qrData);
                var bmp      = qrCode.GetGraphic(6, System.Drawing.Color.Black,
                                                     System.Drawing.Color.White, true);

                // Convert System.Drawing.Bitmap → WPF BitmapSource
                using var memStream = new MemoryStream();
                bmp.Save(memStream, System.Drawing.Imaging.ImageFormat.Png);
                memStream.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memStream;
                bitmapImage.CacheOption  = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                Dispatcher.Invoke(() =>
                {
                    QrCodeImage.Source = bitmapImage;
                    QrPlaceholder.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                // QRCoder not installed – show informative fallback
                Dispatcher.Invoke(() =>
                {
                    QrPlaceholder.Text       = "Install QRCoder NuGet\npackage for QR codes";
                    QrPlaceholder.Visibility = Visibility.Visible;
                    AddLog($"⚠ QR: {ex.Message}", "#F59E0B");
                });
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UI BUTTON HANDLERS
        // ═════════════════════════════════════════════════════════════════════
        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(IpLabel.Text);
                AddLog("📋 URL copied to clipboard.", "#64748B");
            }
            catch { /* ignore */ }
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title      = "Select files to share",
                Multiselect = true,
                Filter     = "All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog() != true) return;

            foreach (string f in dlg.FileNames)
            {
                if (!_sharedFiles.Contains(f))
                {
                    _sharedFiles.Add(f);
                    SharedFilesList.Items.Add(Path.GetFileName(f));
                }
            }
            AddLog($"➕ {dlg.FileNames.Length} file(s) added to share list.", "#64748B");
        }

        private void ClearFiles_Click(object sender, RoutedEventArgs e)
        {
            _sharedFiles.Clear();
            SharedFilesList.Items.Clear();
            AddLog("🗑 Share list cleared.", "#94A3B8");
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogPanel.Children.Clear();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  HELPERS
        // ═════════════════════════════════════════════════════════════════════
        private void AddLog(string message, string hexColor)
        {
            Dispatcher.Invoke(() =>
            {
                var tb = new TextBlock
                {
                    Text       = $"[{DateTime.Now:HH:mm:ss}]  {message}",
                    FontSize   = 12,
                    Foreground = new SolidColorBrush(
                                     (Color)ColorConverter.ConvertFromString(hexColor)),
                    Margin     = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                LogPanel.Children.Add(tb);

                // auto-scroll
                LogScrollViewer.UpdateLayout();
                LogScrollViewer.ScrollToEnd();
            });
        }

        private void UpdateSavePathLabel()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            SavePathLabel.Text = "Desktop";
            SavePathLabel.ToolTip = desktop;
        }

        private static async Task WriteHtml(HttpListenerResponse resp, string html)
        {
            byte[] buf = Encoding.UTF8.GetBytes(html);
            resp.ContentType     = "text/html; charset=utf-8";
            resp.ContentLength64 = buf.Length;
            await resp.OutputStream.WriteAsync(buf, 0, buf.Length);
        }

        /// <summary>Find first occurrence of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
        private static int FindBytes(byte[] haystack, byte[] needle, int start = 0)
        {
            for (int i = start; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>Return a path that doesn't overwrite an existing file.</summary>
        private static string UniqueFilePath(string dir, string filename)
        {
            string path = Path.Combine(dir, filename);
            if (!File.Exists(path)) return path;

            string name = Path.GetFileNameWithoutExtension(filename);
            string ext  = Path.GetExtension(filename);
            for (int i = 1; i < 10000; i++)
            {
                path = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(path)) return path;
            }
            return Path.Combine(dir, filename);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)        return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024} KB";
            return $"{bytes / (1024 * 1024.0):F1} MB";
        }

        private static string HtmlEncode(string s) =>
            s.Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;").Replace("\"","&quot;");

        // ── clean shutdown ────────────────────────────────────────────────────
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            StopServer();
            base.OnClosing(e);
        }
    }
}
