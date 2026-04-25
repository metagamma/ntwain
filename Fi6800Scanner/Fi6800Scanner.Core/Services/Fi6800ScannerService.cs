using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Fi6800Scanner.Core.Capabilities;
using Fi6800Scanner.Core.Models;
using NTwain;
using NTwain.Data;

namespace Fi6800Scanner.Core.Services
{
    /// <summary>
    /// Wrapper de NTwain TwainSession orientado al fi-6800 + PaperStream IP.
    /// Pensado para WinForms (usa WindowsFormsMessageLoopHook).
    /// </summary>
    public class Fi6800ScannerService : IScannerService
    {
        private readonly TwainSession _session;
        private DataSource _source;
        private MessageLoopHook _hook;
        private IntPtr _hwnd;
        private string _tempFolder;
        private int _pageCounter;
        private DateTime _batchStart;
        private int _errorCount;
        private bool _cancelRequested;
        private ScanProfile _activeProfile;
        private Fi6800CapabilitySnapshot _snapshot;

        public bool IsOpen => _source != null && _source.IsOpen;
        public string CurrentSourceName => _source?.Name;

        public event EventHandler<ScannedPage> PageReceived;
        public event EventHandler<BatchProgress> ProgressChanged;
        public event EventHandler<ScanErrorInfo> ErrorOccurred;
        public event EventHandler<BatchSummary> BatchCompleted;

        public Fi6800ScannerService()
        {
            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());
            _session = new TwainSession(appId);

            _session.TransferReady    += OnTransferReady;
            _session.DataTransferred  += OnDataTransferred;
            _session.TransferError    += OnTransferError;
            _session.SourceDisabled   += OnSourceDisabled;
            _session.StateChanged     += (s, e) => { /* opcional: log */ };
        }

        public IList<string> ListSources()
        {
            EnsureDsmOpen(IntPtr.Zero);
            return _session.Select(s => s.Name).ToList();
        }

        public Fi6800CapabilitySnapshot OpenAndProbe(IntPtr hwnd, string sourceNameContains = null)
        {
            EnsureDsmOpen(hwnd);

            // Buscar fi-6800 / PaperStream IP fi-6800; fallback a default
            DataSource pick = null;
            if (!string.IsNullOrEmpty(sourceNameContains))
                pick = _session.FirstOrDefault(s => s.Name.IndexOf(sourceNameContains, StringComparison.OrdinalIgnoreCase) >= 0);

            if (pick == null)
                pick = _session.FirstOrDefault(s => s.Name.IndexOf("fi-6800", StringComparison.OrdinalIgnoreCase) >= 0);

            if (pick == null)
                pick = _session.FirstOrDefault(s => s.Name.IndexOf("PaperStream IP", StringComparison.OrdinalIgnoreCase) >= 0);

            if (pick == null) pick = _session.DefaultSource;
            if (pick == null) throw new InvalidOperationException("No se encontró ningún source TWAIN. Verifica drivers instalados.");

            var rc = pick.Open();
            if (rc != ReturnCode.Success)
                throw new InvalidOperationException("Source no abrió: " + rc);

            _source = pick;
            _snapshot = new ScannerProbe().Probe(_source);
            return _snapshot;
        }

        public ProfileApplyResult ApplyProfile(ScanProfile profile)
        {
            if (_source == null || !_source.IsOpen)
                throw new InvalidOperationException("Source no está abierto. Llama OpenAndProbe primero.");

            _activeProfile = profile;
            var report = ProfileApplicator.Apply(_source, profile, _snapshot);

            return new ProfileApplyResult
            {
                Success = report.Failed.Count == 0,
                Applied = report.Applied,
                Skipped = report.Skipped,
                Failed  = report.Failed
            };
        }

        public void StartScan()
        {
            if (_source == null || !_source.IsOpen)
                throw new InvalidOperationException("Source no está abierto.");
            if (_activeProfile == null)
                throw new InvalidOperationException("No se aplicó ningún perfil.");

            _pageCounter = 0;
            _errorCount = 0;
            _cancelRequested = false;
            _batchStart = DateTime.UtcNow;

            // Carpeta temporal por sesión cuando XferMech.File
            if (_activeProfile.XferMech == XferMech.File)
            {
                _tempFolder = Path.Combine(Path.GetTempPath(), "Fi6800Scanner", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempFolder);
            }

            var rc = _source.Enable(SourceEnableMode.NoUI, false, _hwnd);
            if (rc != ReturnCode.Success)
                throw new InvalidOperationException("Source.Enable falló: " + rc);
        }

        public void CancelScan()
        {
            _cancelRequested = true;
            // TransferReady aplicará e.CancelAll = true en el próximo evento
        }

        public void Close()
        {
            try
            {
                if (_source != null && _source.IsOpen) _source.Close();
            }
            catch { }
            try
            {
                if (_session.State > 2) _session.Close();
            }
            catch
            {
                try { _session.ForceStepDown(2); } catch { }
            }
            CleanupTemp();
        }

        public void Dispose() => Close();

        // ─── Eventos NTwain ─────────────────────────────────────────────

        private void OnTransferReady(object sender, TransferReadyEventArgs e)
        {
            if (_cancelRequested) { e.CancelAll = true; return; }

            // Si XferMech = File, configurar archivo de salida para esta página
            if (_activeProfile != null && _activeProfile.XferMech == XferMech.File && _tempFolder != null)
            {
                try
                {
                    string ext = ExtensionFor(_activeProfile.FileFormat);
                    string fileName = $"page_{(_pageCounter + 1):D5}{ext}";
                    string fullPath = Path.Combine(_tempFolder, fileName);

                    var setup = new TWSetupFileXfer
                    {
                        Format = _activeProfile.FileFormat,
                        FileName = fullPath
                    };
                    _source.DGControl.SetupFileXfer.Set(setup);
                }
                catch (Exception ex)
                {
                    Raise(ErrorOccurred, new ScanErrorInfo { Message = "SetupFileXfer falló: " + ex.Message, Exception = ex });
                }
            }
        }

        private void OnDataTransferred(object sender, DataTransferredEventArgs e)
        {
            try
            {
                var page = BuildScannedPage(e);
                _pageCounter++;

                Raise(PageReceived, page);
                Raise(ProgressChanged, new BatchProgress
                {
                    PagesScanned = _pageCounter,
                    PendingTransfers = SafePendingCount(),
                    LastSide = page.Side
                });
            }
            catch (Exception ex)
            {
                _errorCount++;
                Raise(ErrorOccurred, new ScanErrorInfo
                {
                    Message = "Error procesando página: " + ex.Message,
                    Exception = ex,
                    IsFatal = false
                });
            }
        }

        private void OnTransferError(object sender, TransferErrorEventArgs e)
        {
            _errorCount++;
            string cc = e.SourceStatus != null ? e.SourceStatus.ConditionCode.ToString() : "Unknown";
            Raise(ErrorOccurred, new ScanErrorInfo
            {
                Message = "Transfer error: RC=" + e.ReturnCode + ", CC=" + cc,
                ConditionCode = cc,
                Exception = e.Exception,
                IsFatal = e.ReturnCode == ReturnCode.Failure
            });
        }

        private void OnSourceDisabled(object sender, EventArgs e)
        {
            var summary = new BatchSummary
            {
                TotalPages = _pageCounter,
                Duration = DateTime.UtcNow - _batchStart,
                Errors = _errorCount
            };
            Raise(BatchCompleted, summary);
        }

        // ─── Helpers ───────────────────────────────────────────────────

        private ScannedPage BuildScannedPage(DataTransferredEventArgs e)
        {
            var page = new ScannedPage
            {
                PageNumber = _pageCounter + 1,
                Side = ReadPageSide(e),
                PixelType = e.ImageInfo != null ? e.ImageInfo.PixelType : PixelType.RGB,
                WidthPx = e.ImageInfo != null ? e.ImageInfo.ImageWidth : 0,
                HeightPx = e.ImageInfo != null ? e.ImageInfo.ImageLength : 0,
                DpiX = e.ImageInfo != null ? (int)(float)e.ImageInfo.XResolution : 0,
                DpiY = e.ImageInfo != null ? (int)(float)e.ImageInfo.YResolution : 0
            };

            // Imagen completa
            Bitmap full = null;
            if (e.NativeData != IntPtr.Zero)
            {
                using (var stream = e.GetNativeImageStream())
                {
                    if (stream != null) full = new Bitmap(stream);
                }
            }
            else if (!string.IsNullOrEmpty(e.FileDataPath))
            {
                // Mover el archivo a nuestra carpeta para gestión propia (driver puede borrar el temp)
                page.TempFilePath = e.FileDataPath;
                if (File.Exists(e.FileDataPath))
                {
                    try { full = (Bitmap)Image.FromFile(e.FileDataPath); }
                    catch { /* si no es imagen GDI+ leíble, dejar full=null */ }
                }
            }

            page.FullImage = full;
            page.Thumbnail = full != null ? CreateThumbnail(full, 200, 280) : null;

            // Extended Image Info: patch + barcode + skew
            ReadExtImageInfo(e, page);

            return page;
        }

        private static PageSide ReadPageSide(DataTransferredEventArgs e)
        {
            try
            {
                var infos = e.GetExtImageInfo(ExtendedImageInfo.PageSide).ToList();
                foreach (var info in infos)
                {
                    if (info.InfoID != ExtendedImageInfo.PageSide || info.NumItems == 0) continue;
                    var values = info.ReadValues();
                    if (values != null && values.Count > 0)
                    {
                        ushort v = Convert.ToUInt16(values[0]);
                        return v == 1 ? PageSide.Rear : PageSide.Front;
                    }
                }
            }
            catch { }
            return PageSide.Front;
        }

        private static void ReadExtImageInfo(DataTransferredEventArgs e, ScannedPage page)
        {
            try
            {
                var infos = e.GetExtImageInfo(
                    ExtendedImageInfo.PatchCode,
                    ExtendedImageInfo.SkewFinalAngle,
                    ExtendedImageInfo.BarcodeCount,
                    ExtendedImageInfo.BarcodeText,
                    ExtendedImageInfo.BarcodeType).ToList();

                // Patch
                var patchInfo = infos.FirstOrDefault(i => i.InfoID == ExtendedImageInfo.PatchCode);
                if (patchInfo.NumItems > 0)
                {
                    var values = patchInfo.ReadValues();
                    if (values != null && values.Count > 0)
                    {
                        ushort v = Convert.ToUInt16(values[0]);
                        if (v > 0) page.DetectedPatch = (PatchCode)(v - 1);
                    }
                }

                // Skew
                var skewInfo = infos.FirstOrDefault(i => i.InfoID == ExtendedImageInfo.SkewFinalAngle);
                if (skewInfo.NumItems > 0)
                {
                    var values = skewInfo.ReadValues();
                    if (values != null && values.Count > 0 && values[0] is TWFix32 fix)
                        page.SkewAngleDegrees = (float)fix;
                }

                // Barcodes
                var bcTextInfo = infos.FirstOrDefault(i => i.InfoID == ExtendedImageInfo.BarcodeText);
                var bcTypeInfo = infos.FirstOrDefault(i => i.InfoID == ExtendedImageInfo.BarcodeType);
                if (bcTextInfo.NumItems > 0)
                {
                    var texts = bcTextInfo.ReadValues();
                    var types = bcTypeInfo.NumItems > 0 ? bcTypeInfo.ReadValues() : null;
                    var list = new List<DetectedBarcode>();
                    for (int i = 0; i < texts.Count; i++)
                    {
                        var bc = new DetectedBarcode
                        {
                            Text = texts[i] != null ? texts[i].ToString() : string.Empty
                        };
                        if (types != null && i < types.Count)
                            bc.Type = (BarcodeType)Convert.ToUInt16(types[i]);
                        list.Add(bc);
                    }
                    page.Barcodes = list;
                }
            }
            catch { /* ExtImageInfo es opcional — no fallar la página */ }
        }

        private static Bitmap CreateThumbnail(Bitmap source, int maxW, int maxH)
        {
            float ratio = Math.Min(maxW / (float)source.Width, maxH / (float)source.Height);
            int w = Math.Max(1, (int)(source.Width * ratio));
            int h = Math.Max(1, (int)(source.Height * ratio));
            var thumb = new Bitmap(w, h);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(source, 0, 0, w, h);
            }
            return thumb;
        }

        private int SafePendingCount()
        {
            try
            {
                var caps = _source?.Capabilities;
                // Cap CAP_XFERCOUNT no devuelve "pendientes"; PendingTransferCount solo está en TransferReadyEventArgs
                // Devolvemos -1 si no podemos saber
                return -1;
            }
            catch { return -1; }
        }

        private static string ExtensionFor(FileFormat fmt)
        {
            switch (fmt)
            {
                case FileFormat.Tiff:
                case FileFormat.TiffMulti: return ".tif";
                case FileFormat.Jfif:      return ".jpg";
                case FileFormat.Bmp:       return ".bmp";
                case FileFormat.Png:       return ".png";
                case FileFormat.Pdf:
                case FileFormat.PdfA:
                case FileFormat.PdfA2:     return ".pdf";
                default: return ".bin";
            }
        }

        private void EnsureDsmOpen(IntPtr hwnd)
        {
            if (_session.State >= 3) return;

            // WinForms hook si tenemos hwnd válido; si no, internal hook
            _hwnd = hwnd;
            if (hwnd != IntPtr.Zero)
            {
                _hook = new WindowsFormsMessageLoopHook(hwnd);
                var rc = _session.Open(_hook);
                if (rc != ReturnCode.Success)
                    throw new InvalidOperationException("DSM no abrió: " + rc);
            }
            else
            {
                var rc = _session.Open();
                if (rc != ReturnCode.Success)
                    throw new InvalidOperationException("DSM no abrió: " + rc);
            }
        }

        private void Raise<T>(EventHandler<T> handler, T payload)
        {
            handler?.Invoke(this, payload);
        }

        private void CleanupTemp()
        {
            if (string.IsNullOrEmpty(_tempFolder)) return;
            try { if (Directory.Exists(_tempFolder)) Directory.Delete(_tempFolder, true); } catch { }
            _tempFolder = null;
        }
    }
}
