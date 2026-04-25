using System;
using System.IO;
using System.Windows.Forms;
using Fi6800Scanner.Core.Diagnostics;
using Fi6800Scanner.Core.Models;
using Fi6800Scanner.Core.Services;

namespace Fi6800Scanner.App.Forms
{
    public class DiagnosticsDialog : Form
    {
        private readonly TextBox _txt;

        public DiagnosticsDialog(PreflightResult preflight, Fi6800CapabilitySnapshot snapshot)
        {
            Text = "Diagnóstico — fi-6800 / PaperStream IP";
            Width = 900;
            Height = 700;
            StartPosition = FormStartPosition.CenterParent;

            _txt = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Font = new System.Drawing.Font("Consolas", 9f),
                WordWrap = false
            };

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8, 4, 8, 4)
            };

            var btnClose = new Button { Text = "Cerrar", Width = 90, DialogResult = DialogResult.OK };
            var btnSave = new Button { Text = "Guardar JSON…", Width = 130 };
            btnSave.Click += (s, e) => SaveJson(snapshot);

            bottom.Controls.Add(btnClose);
            bottom.Controls.Add(btnSave);

            Controls.Add(_txt);
            Controls.Add(bottom);

            AcceptButton = btnClose;
            CancelButton = btnClose;

            _txt.Text = BuildReport(preflight, snapshot);
            _txt.SelectionStart = 0;
        }

        private static string BuildReport(PreflightResult pre, Fi6800CapabilitySnapshot snap)
        {
            var w = new StringWriter();
            w.WriteLine("=== PRE-FLIGHT ===");
            w.WriteLine("OS: " + pre.OsVersion);
            w.WriteLine("App x64: " + pre.IsApp64Bit);
            w.WriteLine("PaperStream IP x86 instalado: " + pre.PaperStreamX86Installed);
            w.WriteLine("PaperStream IP x64 instalado: " + pre.PaperStreamX64Installed);
            w.WriteLine("PaperStream version: " + (pre.PaperStreamVersion ?? "(desconocida)"));
            w.WriteLine("TWAINDSM.dll disponible: " + pre.TwainDsmAvailable);
            w.WriteLine("Match arquitectura: " + pre.ArchitectureMatch);
            w.WriteLine();
            if (pre.Warnings.Count > 0)
            {
                w.WriteLine("WARNINGS:");
                foreach (var x in pre.Warnings) w.WriteLine("  - " + x);
                w.WriteLine();
            }
            if (pre.Errors.Count > 0)
            {
                w.WriteLine("ERRORS:");
                foreach (var x in pre.Errors) w.WriteLine("  - " + x);
                w.WriteLine();
            }

            if (snap == null)
            {
                w.WriteLine("(Sin snapshot — escáner no conectado o source no abierto)");
                return w.ToString();
            }

            w.WriteLine("=== SNAPSHOT DEL ESCÁNER ===");
            w.WriteLine("Source: " + snap.ScannerName);
            w.WriteLine("Manufacturer: " + snap.Manufacturer);
            w.WriteLine("Family: " + snap.ProductFamily);
            w.WriteLine("Driver version: " + snap.DriverVersion);
            w.WriteLine("Protocol: " + (snap.ProtocolVersion ?? "?"));
            w.WriteLine();

            w.WriteLine("Pixel types: " + string.Join(", ", snap.PixelTypes));
            w.WriteLine("Bit depths: " + string.Join(", ", snap.BitDepths));
            w.WriteLine();

            w.WriteLine("Resolución (DPI): " +
                (snap.ResolutionMin.HasValue ? snap.ResolutionMin.Value.ToString() : "?") + " - " +
                (snap.ResolutionMax.HasValue ? snap.ResolutionMax.Value.ToString() : "?") +
                " (valores: " + snap.ResolutionValues.Count + ")");
            w.WriteLine();

            w.WriteLine("Tamaños: " + string.Join(", ", snap.SupportedSizes));
            w.WriteLine("Physical max: " +
                (snap.PhysicalWidthInches.HasValue ? snap.PhysicalWidthInches.Value.ToString("F2") : "?") + "\" × " +
                (snap.PhysicalHeightInches.HasValue ? snap.PhysicalHeightInches.Value.ToString("F2") : "?") + "\"");
            w.WriteLine();

            w.WriteLine("Compresiones: " + string.Join(", ", snap.Compressions));
            w.WriteLine("File formats: " + string.Join(", ", snap.FileFormats));
            w.WriteLine();

            w.WriteLine("Patch codes: " + string.Join(", ", snap.PatchCodes));
            w.WriteLine("Barcodes: " + string.Join(", ", snap.Barcodes));
            w.WriteLine("Ext image info: " + string.Join(", ", snap.ExtImageInfo));
            w.WriteLine();

            w.WriteLine("=== FEATURES ===");
            var f = snap.Features;
            w.WriteLine("  Duplex: " + f.Duplex);
            w.WriteLine("  AutoColor: " + f.AutoColor);
            w.WriteLine("  AutoDeskew: " + f.AutoDeskew);
            w.WriteLine("  AutoRotate: " + f.AutoRotate);
            w.WriteLine("  AutoBorder: " + f.AutoBorder);
            w.WriteLine("  DiscardBlank: " + f.DiscardBlank);
            w.WriteLine("  DoubleFeedDetection: " + f.DoubleFeedDetection +
                        " (Length=" + f.DoubleFeedByLength + ", Ultrasonic=" + f.DoubleFeedByUltrasonic + ")");
            w.WriteLine("  Imprinter: " + f.Imprinter);
            w.WriteLine("  PatchDetection: " + f.PatchDetection);
            w.WriteLine("  BarcodeDetection: " + f.BarcodeDetection);
            w.WriteLine("  JobControl: " + f.JobControl);
            w.WriteLine("  CustomDsData: " + f.CustomDsData);
            w.WriteLine("  ExtImageInfo: " + f.ExtImageInfo);
            w.WriteLine("  Threshold/Brightness/Contrast/Gamma: " +
                f.Threshold + "/" + f.Brightness + "/" + f.Contrast + "/" + f.Gamma);
            w.WriteLine("  Filter (drop-out): " + f.Filter);
            w.WriteLine("  JpegQuality: " + f.JpegQuality + ", JpegSubsampling: " + f.JpegSubsampling);
            w.WriteLine("  Mirror: " + f.Mirror + ", Rotation: " + f.Rotation + ", Orientation: " + f.Orientation);
            w.WriteLine("  LongPage detected: " + f.LongPageDetected);
            w.WriteLine("  MultiStream detected: " + f.MultiStreamDetected);
            w.WriteLine();

            w.WriteLine("=== CAPS SOPORTADAS (" + snap.SupportedCapIds.Count + ") ===");
            foreach (var id in snap.SupportedCapIds) w.Write(id + "  ");
            w.WriteLine();
            w.WriteLine();

            w.WriteLine("=== CAPS CUSTOM (rango ≥ 0x8000) — " + snap.CustomCapIds.Count + " ===");
            foreach (var c in snap.CustomCaps)
            {
                w.WriteLine("  " + c.Id + " (" + c.IdNumeric + ")");
                w.WriteLine("    " + c.Notes);
            }

            return w.ToString();
        }

        private void SaveJson(Fi6800CapabilitySnapshot snap)
        {
            if (snap == null)
            {
                MessageBox.Show("No hay snapshot disponible.", "Diagnóstico", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                FileName = "fi6800_snapshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SnapshotJsonExporter.Save(snap, dlg.FileName);
                    MessageBox.Show("Snapshot guardado.", "Diagnóstico", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error guardando: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
