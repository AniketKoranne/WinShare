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

namespace WinShare
{
    public partial class MainWindow : Window
    {
        private HttpListener? _listener;
        private string _localIp = "127.0.0.1";
        private string _port = "8080";
        private bool _running = false;

        private readonly List<string> _sharedFiles = new();
        private int _receivedCount = 0;
        private int _sentCount = 0;

        public MainWindow()
        {
            InitializeComponent();
            _localIp = DetectWifiIP();
            UpdateSavePathLabel();
        }

        private static string DetectWifiIP()
        {
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
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        return ip.ToString();
            }
            catch { }
            return "127.0.0.1";
        }

        private void StartServer_Click(object sender, RoutedEventArgs e)
        {
            _port = PortBox.Text.Trim();
            if (!int.TryParse(_port, out int p) || p < 1024 || p > 65535)
            {
                MessageBox.Show("Enter a valid port number (1024 – 65535).", "Invalid Port", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _localIp = DetectWifiIP();

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://{_localIp}:{_port}/");
                _listener.Start();
                _running = true;

                Task.Run(ListenLoop);

                string url = $"http://{_localIp}:{_port}";
                IpLabel.Text = url;
                StatusLabel.Text = "Running";
                StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                StartBtn.IsEnabled = false;
                StopBtn.IsEnabled = true;
                CopyUrlBtn.IsEnabled = true;
                AddFilesBtn.IsEnabled = true;
                PortBox.IsEnabled = false;

                ShowQrCode(url);
                AddLog($"✅ Server started on {url}", "#10B981");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not start server:\n{ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopServer_Click(object sender, RoutedEventArgs e) => StopServer();

        private void StopServer()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;

            Dispatcher.Invoke(() =>
            {
                StatusLabel.Text = "Stopped";
                StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                IpLabel.Text = "—";
                StartBtn.IsEnabled = true;
                StopBtn.IsEnabled = false;
                CopyUrlBtn.IsEnabled = false;
                AddFilesBtn.IsEnabled = false;
                PortBox.IsEnabled = true;
                QrCodeImage.Source = null;
                QrPlaceholder.Visibility = Visibility.Visible;
                AddLog("⏹ Server stopped.", "#94A3B8");
            });
        }

        private async Task ListenLoop()
        {
            while (_running && _listener != null && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(ctx));
                }
                catch { }
            }
        }

        private async void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            // Ensure headers fix browser connection sticking
            resp.AddHeader("Cache-Control", "no-store, no-cache, must-revalidate");
            resp.AddHeader("Pragma", "no-cache");
            resp.KeepAlive = true;

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
                    bool success = await ReceiveUploadStreaming(req);
                    if (success)
                    {
                        await WriteHtml(resp, BuildSuccessPage());
                    }
                    else
                    {
                        resp.StatusCode = 400;
                        await WriteHtml(resp, "<html><body><h2>Upload Processing Failed</h2></body></html>");
                    }
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

        // Low-Memory RAM Efficient Multipart Stream Processing File Receiver
        private async Task<bool> ReceiveUploadStreaming(HttpListenerRequest request)
        {
            string ct = request.ContentType ?? "";
            if (!ct.Contains("boundary=")) return false;

            string boundary = "--" + ct.Split(new[] { "boundary=" }, StringSplitOptions.None)[1].Split(';')[0].Trim();
            byte[] boundaryBytes = Encoding.UTF8.GetBytes(boundary);

            Stream input = request.InputStream;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            // Read first block to intercept headers safely
            byte[] buffer = new byte[1024 * 1024]; // Sliding window optimization
            int bytesRead = await input.ReadAsync(buffer, 0, buffer.Length);
            if (bytesRead <= 0) return false;

            string initialChunkText = Encoding.UTF8.GetString(buffer, 0, Math.Min(bytesRead, 4096));

            // Extract filename safely
            string filename = $"upload_{DateTime.Now:yyyyMMdd_HHmmss}.dat";
            const string fnKey = "filename=\"";
            int fi = initialChunkText.IndexOf(fnKey, StringComparison.Ordinal);
            if (fi != -1)
            {
                int fs = fi + fnKey.Length;
                int fe = initialChunkText.IndexOf('"', fs);
                if (fe != -1)
                    filename = Path.GetFileName(initialChunkText.Substring(fs, fe - fs));
            }

            // Find start of file data payload (\r\n\r\n)
            byte[] separator = { 13, 10, 13, 10 };
            int dataStartOffset = FindBytes(buffer, separator, 0, bytesRead);
            if (dataStartOffset == -1) return false;
            dataStartOffset += separator.Length;

            string targetPath = UniqueFilePath(desktop, filename);
            long totalWrittenBytes = 0;

            using (FileStream fs = File.Create(targetPath))
            {
                // Write leftover bytes from first read burst window
                int remainingInInitial = bytesRead - dataStartOffset;
                if (remainingInInitial > 0)
                {
                    // Check if closing boundary falls inside initial chunk block
                    int tailIndex = FindBytes(buffer, boundaryBytes, dataStartOffset, bytesRead);
                    if (tailIndex != -1)
                    {
                        int actualLen = tailIndex - dataStartOffset - 2; // Trim trailing \r\n
                        if (actualLen > 0) await fs.WriteAsync(buffer, dataStartOffset, actualLen);
                        totalWrittenBytes += Math.Max(0, actualLen);
                        goto CompleteWriteCycle;
                    }
                    else
                    {
                        // Safely write window frame, leaving back margin for cross-block edge boundary matching
                        int safeWrite = remainingInInitial - boundaryBytes.Length - 4;
                        if (safeWrite > 0)
                        {
                            await fs.WriteAsync(buffer, dataStartOffset, safeWrite);
                            totalWrittenBytes += safeWrite;
                            // Push back edge window
                            Array.Copy(buffer, dataStartOffset + safeWrite, buffer, 0, remainingInInitial - safeWrite);
                            bytesRead = remainingInInitial - safeWrite;
                        }
                        else
                        {
                            Array.Copy(buffer, dataStartOffset, buffer, 0, remainingInInitial);
                            bytesRead = remainingInInitial;
                        }
                    }
                }
                else
                {
                    bytesRead = 0;
                }

                // Streaming Loop for incoming fragments
                while (true)
                {
                    int spaceAvailable = buffer.Length - bytesRead;
                    int readCurrent = await input.ReadAsync(buffer, bytesRead, spaceAvailable);
                    if (readCurrent <= 0) break;

                    bytesRead += readCurrent;

                    int boundaryMatchPos = FindBytes(buffer, boundaryBytes, 0, bytesRead);
                    if (boundaryMatchPos != -1)
                    {
                        int finalWriteLength = boundaryMatchPos - 2; // Clean \r\n padding margin
                        if (finalWriteLength > 0)
                        {
                            await fs.WriteAsync(buffer, 0, finalWriteLength);
                            totalWrittenBytes += finalWriteLength;
                        }
                        break; // Streaming block closed successfully
                    }

                    // Keep a trailing buffer window margin active to avoid missing split boundary definitions
                    int safeWriteLength = bytesRead - (boundaryBytes.Length + 4);
                    if (safeWriteLength > 0)
                    {
                        await fs.WriteAsync(buffer, 0, safeWriteLength);
                        totalWrittenBytes += safeWriteLength;

                        Array.Copy(buffer, safeWriteLength, buffer, 0, bytesRead - safeWriteLength);
                        bytesRead -= safeWriteLength;
                    }
                }
            }

        CompleteWriteCycle:
            _receivedCount++;
            Dispatcher.Invoke(() =>
            {
                AddLog($"📥 Received: {Path.GetFileName(targetPath)} ({FormatSize(totalWrittenBytes)})", "#3B82F6");
                ReceivedCount.Text = $"{_receivedCount} file{(_receivedCount == 1 ? "" : "s")}";
            });

            return true;
        }

        private async Task ServeFileDownload(HttpListenerRequest req, HttpListenerResponse resp)
        {
            string? fname = req.QueryString["f"];
            if (string.IsNullOrEmpty(fname)) { resp.StatusCode = 400; return; }

            string? fullPath = _sharedFiles.Find(p => Path.GetFileName(p).Equals(fname, StringComparison.OrdinalIgnoreCase));

            if (fullPath == null || !File.Exists(fullPath))
            {
                resp.StatusCode = 404;
                await WriteHtml(resp, "<html><body><h2>File not found.</h2></body></html>");
                return;
            }

            long fileLength = new FileInfo(fullPath).Length;
            resp.ContentType = "application/octet-stream";
            resp.AddHeader("Content-Disposition", $"attachment; filename=\"{Uri.EscapeDataString(Path.GetFileName(fullPath))}\"");
            resp.ContentLength64 = fileLength;

            using (FileStream fs = File.OpenRead(fullPath))
            {
                byte[] buffer = new byte[1024 * 1024]; // High speed 64KB out-burst window
                int bytesRead;
                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await resp.OutputStream.WriteAsync(buffer, 0, bytesRead);
                }
            }

            _sentCount++;
            Dispatcher.Invoke(() =>
            {
                AddLog($"📤 Sent: {Path.GetFileName(fullPath)} ({FormatSize(fileLength)})", "#10B981");
                SentCount.Text = $"{_sentCount} file{(_sentCount == 1 ? "" : "s")}";
            });
        }

        // Web View Generator UIs with Dynamic Mobile Execution Enhancements
        private static string HtmlShell(string title, string body) => $@"<!DOCTYPE html>
<html lang='en'>
<head>
  <meta charset='UTF-8'/>
  <meta name='viewport' content='width=device-width,initial-scale=1.0,maximum-scale=1.0'/>
  <title>{title} — WinShare</title>
  <style>
    *{{box-sizing:border-box;margin:0;padding:0}}
    body{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#F8FAFC;color:#0F172A;min-height:100vh}}
    .top{{background:#2563EB;color:#fff;padding:20px;display:flex;align-items:center;gap:12px;box-shadow:0 4px 12px rgba(37,99,235,0.15)}}
    .top h1{{font-size:1.3rem;font-weight:700;letter-spacing:-0.025em}}
    .top span{{font-size:1.8rem}}
    nav{{display:flex;background:#fff;border-bottom:1px solid #E2E8F0;position:sticky;top:0;z-index:50}}
    nav a{{flex:1;padding:16px;text-align:center;text-decoration:none;color:#64748B;font-size:.95rem;font-weight:600;transition:all 0.2s}}
    nav a.active{{color:#2563EB;border-bottom:3px solid #2563EB;background:rgba(37,99,235,0.02)}}
    .content{{padding:20px;max-width:540px;margin:auto}}
    .card{{background:#fff;border-radius:16px;padding:24px;box-shadow:0 4px 6px -1px rgba(0,0,0,0.05),0 2px 4px -1px rgba(0,0,0,0.03);margin-bottom:20px;border:1px solid #E2E8F0}}
    .card h2{{font-size:1.1rem;font-weight:700;margin-bottom:16px;color:#1E293B}}
    .btn{{display:flex;align-items:center;justify-content:center;width:100%;padding:14px;border:none;border-radius:12px;font-size:1rem;font-weight:600;cursor:pointer;transition:all 0.15s ease}}
    .btn-blue{{background:#2563EB;color:#fff}}
    .btn-blue:hover{{background:#1D4ED8}}
    .btn-green{{background:#10B981;color:#fff}}
    .btn:disabled{{background:#94A3B8;cursor:not-allowed;opacity:0.6}}
    .btn:active{{transform:scale(0.98)}}
    .file-item{{display:flex;align-items:center;justify-content:space-between;padding:14px;border-radius:12px;background:#F1F5F9;border:1px solid #E2E8F0;margin-bottom:12px}}
    .file-name{{font-size:.95rem;color:#334155;word-break:break-all;flex:1;margin-right:12px;font-weight:500}}
    .dl-btn{{padding:10px 18px;background:#2563EB;color:#fff;border:none;border-radius:10px;font-weight:600;font-size:.85rem;cursor:pointer}}
    .empty{{text-align:center;color:#94A3B8;padding:40px 20px;font-size:.95rem}}
    #drop-zone{{border:2px dashed #CBD5E1;border-radius:14px;padding:40px 20px;text-align:center;transition:all .2s ease;background:#F8FAFC;cursor:pointer}}
    #drop-zone.over{{border-color:#2563EB;background:#EFF6FF;transform:scale(1.01)}}
    #drop-zone p{{color:#475569;font-size:.95rem;margin-top:10px;font-weight:500}}
    #drop-zone span{{font-size:2.8rem}}
    #file-input{{display:none}}
    #preview{{margin-top:14px;font-size:.9rem;color:#2563EB;font-weight:600;word-break:break-all;max-height:100px;overflow-y:auto}}
    #progress-wrap{{display:none;margin:20px 0}}
    #progress-bar{{height:8px;background:#E2E8F0;border-radius:4px;overflow:hidden}}
    #progress-fill{{height:100%;background:#2563EB;width:0%;transition:width 0.1s linear}}
    #progress-text{{font-size:.85rem;color:#475569;margin-top:6px;display:flex;justify-content:between;font-weight:500}}
    .success{{background:#DCFCE7;border:1px solid #BBF7D0;color:#166534;border-radius:12px;padding:16px;text-align:center;font-weight:600;font-size:1.05rem}}
    .badge{{display:inline-block;background:#E0F2FE;color:#0369A1;font-size:.75rem;font-weight:700;padding:3px 10px;border-radius:12px;margin-left:8px;white-space:nowrap}}
  </style>
</head>
<body>
  <div class='top'><span>📡</span><h1>WinShare Connect</h1></div>
  <nav>
    <a href='/' {(title == "Upload" ? "class='active'" : "")}>📥 Send to PC</a>
    <a href='/files' {(title == "Files" ? "class='active'" : "")}>📤 Get from PC</a>
  </nav>
  <div class='content'>{body}</div>
</body>
</html>";

        private string BuildUploadPage() => HtmlShell("Upload", @"
<div class='card'>
  <h2>Upload files to PC</h2>
  <div id='drop-zone' onclick='document.getElementById(""file-input"").click()'>
    <span>📁</span>
    <p>Tap to choose files or drop here</p>
  </div>
  <input type='file' id='file-input' multiple onchange='onFilePicked(this)'/>
  <div id='preview'></div>
  
  <div id='progress-wrap'>
    <div id='progress-bar'><div id='progress-fill'></div></div>
    <div id='progress-text'>Preparing...</div>
  </div>
  
  <button id='upload-btn' class='btn btn-blue' style='margin-top:16px;' onclick='doUpload()' disabled>Upload to Computer</button>
</div>
<script>
var files=[];
function onFilePicked(inp){
  files=Array.from(inp.files);
  updateDisplay();
}
var dz=document.getElementById('drop-zone');
dz.addEventListener('dragover',e=>{e.preventDefault();dz.classList.add('over')});
dz.addEventListener('dragleave',()=>dz.classList.remove('over'));
dz.addEventListener('drop',e=>{
  e.preventDefault();dz.classList.remove('over');
  files=Array.from(e.dataTransfer.files);
  updateDisplay();
});
function updateDisplay(){
  var btn = document.getElementById('upload-btn');
  if(files.length === 0){
    document.getElementById('preview').textContent='';
    btn.disabled = true;
    return;
  }
  btn.disabled = false;
  document.getElementById('preview').textContent = files.length + ' file(s) selected: ' + files.map(f=>f.name).join(', ');
}
async function doUpload(){
  if(!files.length) return;
  document.getElementById('upload-btn').disabled = true;
  var pw=document.getElementById('progress-wrap');
  var pf=document.getElementById('progress-fill');
  var pt=document.getElementById('progress-text');
  pw.style.display='block';
  
  for(var i=0;i<files.length;i++){
    var fd=new FormData();
    fd.append('file',files[i],files[i].name);
    
    await new Promise((res,rej)=>{
      var xhr=new XMLHttpRequest();
      xhr.open('POST','/');
      
      xhr.upload.onprogress=e=>{
        if(e.lengthComputable){
          var pct=Math.round(e.loaded/e.total*100);
          pf.style.width=pct+'%';
          pt.innerHTML='<span>Uploading ('+(i+1)+'/'+files.length+'): ' + files[i].name + '</span> <span>' + pct+'%</span>';
        }
      };
      
      xhr.onload=()=>{
         if(xhr.status >= 200 && xhr.status < 300) res();
         else rej(new Error('Server error'));
      };
      xhr.onerror=()=>rej(new Error('Network loss'));
      xhr.send(fd);
    });
  }
  pw.style.display='none';
  document.querySelector('.card').innerHTML=""<div class='success'>✨ Success! All files sent safely to PC.</div><br/><button class='btn btn-blue' onclick='window.location.reload()'>Transfer More Files</button>"";
}
</script>");

        private string BuildDownloadPage()
        {
            var sb = new StringBuilder();
            sb.Append("<div class='card'><h2>Available Downloads on PC</h2>");

            int activeCount = 0;
            foreach (string fp in _sharedFiles)
            {
                if (!File.Exists(fp)) continue;
                activeCount++;
                var info = new FileInfo(fp);
                string name = info.Name;
                string size = FormatSize(info.Length);
                string enc = Uri.EscapeDataString(name);
                sb.Append($"<div class='file-item'>" +
                          $"<span class='file-name'>{HtmlEncode(name)}" +
                          $"<span class='badge'>{size}</span></span>" +
                          $"<a href='/dl?f={enc}' style='text-decoration:none;'><button class='dl-btn'>Download</button></a></div>");
            }

            if (activeCount == 0)
            {
                sb.Append("<div class='empty'>No files currently exposed by host.<br/><small style='color:#94A3B8; font-size:0.8rem;'>Add items using the WinShare Windows layout interface.</small></div>");
            }

            sb.Append("</div>");
            return HtmlShell("Files", sb.ToString());
        }

        private static string BuildSuccessPage() => HtmlShell("Upload", @"
<div class='card'>
  <div class='success'>🎉 Transfer Completed Successfully!</div>
  <br/>
  <a href='/' style='text-decoration:none;'><button class='btn btn-blue'>Send Another File</button></a>
  <a href='/files' style='text-decoration:none; margin-top:12px; display:block;'><button class='btn btn-green'>View Files On PC</button></a>
</div>");

        private void ShowQrCode(string url)
        {
            try
            {
                using var qrGenerator = new QRCoder.QRCodeGenerator();
                var qrData = qrGenerator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
                var qrCode = new QRCoder.QRCode(qrData);
                var bmp = qrCode.GetGraphic(6, System.Drawing.Color.FromArgb(15, 23, 42), System.Drawing.Color.White, true);

                using var memStream = new MemoryStream();
                bmp.Save(memStream, System.Drawing.Imaging.ImageFormat.Png);
                memStream.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memStream;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                Dispatcher.Invoke(() =>
                {
                    QrCodeImage.Source = bitmapImage;
                    QrPlaceholder.Visibility = Visibility.Collapsed;
                });
            }
            catch
            {
                Dispatcher.Invoke(() =>
                {
                    QrPlaceholder.Text = "Install QRCoder NuGet\npackage for QR codes";
                    QrPlaceholder.Visibility = Visibility.Visible;
                });
            }
        }

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(IpLabel.Text);
                AddLog("📋 Connection link copied.", "#475569");
            }
            catch { }
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select files to share",
                Multiselect = true,
                Filter = "All Files (*.*)|*.*"
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
            AddLog($"➕ Added {dlg.FileNames.Length} files to transfer list.", "#475569");
        }

        private void ClearFiles_Click(object sender, RoutedEventArgs e)
        {
            _sharedFiles.Clear();
            SharedFilesList.Items.Clear();
            AddLog("🗑 Download payload list cleared.", "#94A3B8");
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e) => LogPanel.Children.Clear();

        private void AddLog(string message, string hexColor)
        {
            Dispatcher.Invoke(() =>
            {
                var tb = new TextBlock
                {
                    Text = $"[{DateTime.Now:HH:mm:ss}]  {message}",
                    FontSize = 12,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor)),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                LogPanel.Children.Add(tb);
                LogScrollViewer.UpdateLayout();
                LogScrollViewer.ScrollToEnd();
            });
        }

        private void UpdateSavePathLabel()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            SavePathLabel.Text = "Desktop Target";
            SavePathLabel.ToolTip = desktop;
        }

        private static async Task WriteHtml(HttpListenerResponse resp, string html)
        {
            byte[] buf = Encoding.UTF8.GetBytes(html);
            resp.ContentType = "text/html; charset=utf-8";
            resp.ContentLength64 = buf.Length;
            await resp.OutputStream.WriteAsync(buf, 0, buf.Length);
        }

        private static int FindBytes(byte[] haystack, byte[] needle, int start, int length)
        {
            int limit = length - needle.Length;
            for (int i = start; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static string UniqueFilePath(string dir, string filename)
        {
            string path = Path.Combine(dir, filename);
            if (!File.Exists(path)) return path;

            string name = Path.GetFileNameWithoutExtension(filename);
            string ext = Path.GetExtension(filename);
            for (int i = 1; i < 10000; i++)
            {
                path = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(path)) return path;
            }
            return Path.Combine(dir, filename);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024 * 1024.0):F1} MB";
            return $"{bytes / (1024 * 1024 * 1024.0):F2} GB";
        }

        private static string HtmlEncode(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            StopServer();
            base.OnClosing(e);
        }
    }
}