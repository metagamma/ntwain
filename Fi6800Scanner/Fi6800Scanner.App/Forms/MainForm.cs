using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Fi6800Scanner.App.Controls;
using Fi6800Scanner.Core.Diagnostics;
using Fi6800Scanner.Core.Models;
using Fi6800Scanner.Core.Services;
using NTwain.Data;

namespace Fi6800Scanner.App.Forms
{
    public class MainForm : Form
    {
        private readonly IScannerService _scanner;
        private readonly PageThumbnailListView _thumbs;
        private readonly ImageViewerPanel _viewer;
        private readonly StatusStrip _status;
        private readonly ToolStripStatusLabel _lblStatus;
        private readonly ToolStripStatusLabel _lblSource;
        private readonly ToolStripStatusLabel _lblPages;
        private readonly ToolStripStatusLabel _lblErrors;

        private PreflightResult _preflight;
        private Fi6800CapabilitySnapshot _snapshot;
        private SynchronizationContext _uiContext;

        public MainForm()
        {
            Text = "Fi6800 Scanner — NTwain v3 + PaperStream IP + Cyotek";
            Width = 1280;
            Height = 800;
            StartPosition = FormStartPosition.CenterScreen;

            _scanner = new Fi6800ScannerService();
            _scanner.PageReceived    += OnPageReceived;
            _scanner.ProgressChanged += OnProgressChanged;
            _scanner.ErrorOccurred   += OnErrorOccurred;
            _scanner.BatchCompleted  += OnBatchCompleted;

            // ─── Layout ─────────────────────────────────────────
            var toolbar = BuildToolbar();

            _thumbs = new PageThumbnailListView { Dock = DockStyle.Fill };
            _thumbs.PageSelected += (s, page) => _viewer.Display(page.FullImage);

            _viewer = new ImageViewerPanel();

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 280,
                FixedPanel = FixedPanel.Panel1
            };
            split.Panel1.Controls.Add(_thumbs);
            split.Panel2.Controls.Add(_viewer);

            _status = new StatusStrip();
            _lblStatus = new ToolStripStatusLabel("Listo");
            _lblSource = new ToolStripStatusLabel("Source: (no abierto)") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _lblPages  = new ToolStripStatusLabel("Páginas: 0");
            _lblErrors = new ToolStripStatusLabel("Errores: 0");
            _status.Items.AddRange(new ToolStripItem[]
            {
                _lblStatus, new ToolStripSeparator(), _lblSource, _lblPages, new ToolStripSeparator(), _lblErrors
            });

            Controls.Add(split);
            Controls.Add(_status);
            Controls.Add(toolbar);

            // SyncContext para marshalling cross-thread (NTwain dispara en thread del hook)
            Load += OnLoadCaptureContext;
            FormClosing += OnFormClosing;
        }

        private ToolStrip BuildToolbar()
        {
            var t = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                ImageScalingSize = new Size(24, 24)
            };
            t.Items.Add(new ToolStripButton("1. Pre-flight",        null, OnPreflight)        { DisplayStyle = ToolStripItemDisplayStyle.Text });
            t.Items.Add(new ToolStripButton("2. Discover (probe)",  null, OnDiscover)         { DisplayStyle = ToolStripItemDisplayStyle.Text });
            t.Items.Add(new ToolStripSeparator());
            t.Items.Add(new ToolStripButton("Escanear",             null, OnScan)             { DisplayStyle = ToolStripItemDisplayStyle.Text });
            t.Items.Add(new ToolStripButton("Cancelar",             null, OnCancel)           { DisplayStyle = ToolStripItemDisplayStyle.Text });
            t.Items.Add(new ToolStripSeparator());
            t.Items.Add(new ToolStripButton("Limpiar",              null, OnClear)            { DisplayStyle = ToolStripItemDisplayStyle.Text });
            t.Items.Add(new ToolStripButton("Guardar lote…",        null, OnSaveBatch)        { DisplayStyle = ToolStripItemDisplayStyle.Text });
            t.Items.Add(new ToolStripSeparator());
            t.Items.Add(new ToolStripButton("Diagnóstico",          null, OnDiagnostics)      { DisplayStyle = ToolStripItemDisplayStyle.Text });
            return t;
        }

        private void OnLoadCaptureContext(object sender, EventArgs e)
        {
            _uiContext = SynchronizationContext.Current;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            try { _scanner.Dispose(); } catch { }
        }

        // ─── Acciones del toolbar ───────────────────────────────────────

        private void OnPreflight(object sender, EventArgs e)
        {
            _preflight = PreflightChecker.Run();
            using (var dlg = new DiagnosticsDialog(_preflight, _snapshot))
                dlg.ShowDialog(this);
            UpdateStatus();
        }

        private void OnDiscover(object sender, EventArgs e)
        {
            try
            {
                _lblStatus.Text = "Descubriendo…";
                Application.DoEvents();

                if (_preflight == null) _preflight = PreflightChecker.Run();
                if (!_preflight.ArchitectureMatch)
                {
                    MessageBox.Show(this,
                        "Mismatch de arquitectura. Revisa el diagnóstico.\n\n" + string.Join("\n", _preflight.Errors),
                        "Pre-flight", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _snapshot = _scanner.OpenAndProbe(Handle);
                _lblSource.Text = "Source: " + _snapshot.ScannerName;
                _lblStatus.Text = "Discovery completo. " + _snapshot.SupportedCapIds.Count + " caps detectadas.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error en discovery: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _lblStatus.Text = "Error: " + ex.Message;
            }
        }

        private void OnScan(object sender, EventArgs e)
        {
            if (!_scanner.IsOpen)
            {
                MessageBox.Show(this, "Primero ejecuta Discover.", "Escanear", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Perfil default: RGB 300 DPI A4 duplex con auto-procesos y XferMech.File
            var profile = BuildDefaultProfile();
            var apply = _scanner.ApplyProfile(profile);

            if (apply.Failed.Count > 0)
            {
                var msg = "Algunas caps fallaron al aplicar:\n" + string.Join("\n", apply.Failed) +
                          "\n\n¿Continuar con el escaneo?";
                if (MessageBox.Show(this, msg, "Aplicar perfil", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            try
            {
                _lblStatus.Text = "Escaneando…";
                _scanner.StartScan();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error iniciando scan: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _lblStatus.Text = "Error: " + ex.Message;
            }
        }

        private void OnCancel(object sender, EventArgs e)
        {
            _scanner.CancelScan();
            _lblStatus.Text = "Cancelando…";
        }

        private void OnClear(object sender, EventArgs e)
        {
            _thumbs.Clear();
            _viewer.Display(null);
            _lblPages.Text = "Páginas: 0";
        }

        private void OnSaveBatch(object sender, EventArgs e)
        {
            if (_thumbs.Count == 0)
            {
                MessageBox.Show(this, "Sin páginas para guardar.", "Guardar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            MessageBox.Show(this,
                "Guardado de lote (TIFF multi-página / PDF) pendiente de Fase 9.\nPor ahora: usa 'Guardar como…' por página.",
                "Guardar lote", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnDiagnostics(object sender, EventArgs e)
        {
            if (_preflight == null) _preflight = PreflightChecker.Run();
            using (var dlg = new DiagnosticsDialog(_preflight, _snapshot))
                dlg.ShowDialog(this);
        }

        // ─── Eventos del scanner ────────────────────────────────────────

        private void OnPageReceived(object sender, ScannedPage page)
        {
            // NTwain dispara estos eventos potencialmente en thread del hook
            // (con WindowsFormsMessageLoopHook normalmente es UI thread, pero por seguridad marshall)
            Post(() =>
            {
                _thumbs.Add(page);
                _viewer.Display(page.FullImage);
            });
        }

        private void OnProgressChanged(object sender, BatchProgress p)
        {
            Post(() =>
            {
                _lblPages.Text = "Páginas: " + p.PagesScanned;
                _lblStatus.Text = "Escaneando… página " + p.PagesScanned;
            });
        }

        private void OnErrorOccurred(object sender, ScanErrorInfo err)
        {
            Post(() =>
            {
                int count = int.Parse(_lblErrors.Text.Replace("Errores: ", ""));
                _lblErrors.Text = "Errores: " + (count + 1);
                _lblStatus.Text = "⚠ " + err.Message;
            });
        }

        private void OnBatchCompleted(object sender, BatchSummary s)
        {
            Post(() =>
            {
                _lblStatus.Text = string.Format(
                    "Lote completo: {0} páginas en {1:0.0}s, {2} errores",
                    s.TotalPages, s.Duration.TotalSeconds, s.Errors);
            });
        }

        // ─── Helpers ────────────────────────────────────────────────────

        private void Post(Action a)
        {
            if (_uiContext != null) _uiContext.Post(_ => a(), null);
            else if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        private void UpdateStatus()
        {
            if (_preflight == null) { _lblStatus.Text = "Pendiente pre-flight"; return; }
            _lblStatus.Text = _preflight.Success ? "Pre-flight OK" : ("Pre-flight con errores: " + string.Join("; ", _preflight.Errors));
        }

        private static ScanProfile BuildDefaultProfile()
        {
            return new ScanProfile
            {
                Name = "Default RGB 300 DPI",
                PixelType = PixelType.RGB,
                Dpi = 300,
                PaperSize = SupportedSize.A4,
                Duplex = true,
                AutoDeskew = true,
                AutoRotate = true,
                AutoBorder = true,
                DiscardBlankPages = false,
                XferMech = XferMech.Native,
                FileFormat = FileFormat.Tiff,
                Compression = CompressionType.None,
                PageCount = -1
            };
        }
    }
}
