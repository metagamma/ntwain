using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Cyotek.Windows.Forms;

namespace Fi6800Scanner.App.Controls
{
    public class ImageViewerPanel : UserControl
    {
        private readonly ImageBox _imageBox;
        private readonly ToolStrip _toolbar;
        private readonly StatusStrip _statusBar;
        private readonly ToolStripStatusLabel _lblPosition;
        private readonly ToolStripStatusLabel _lblZoom;
        private readonly ToolStripStatusLabel _lblSize;

        public event EventHandler<Image> CropRequested;

        public ImageViewerPanel()
        {
            Dock = DockStyle.Fill;

            _toolbar = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                ImageScalingSize = new Size(20, 20)
            };
            _toolbar.Items.Add(new ToolStripButton("Fit",      null, (s, e) => _imageBox.ZoomToFit())          { DisplayStyle = ToolStripItemDisplayStyle.Text });
            _toolbar.Items.Add(new ToolStripButton("1:1",      null, (s, e) => _imageBox.ActualSize())         { DisplayStyle = ToolStripItemDisplayStyle.Text });
            _toolbar.Items.Add(new ToolStripButton("Zoom +",   null, (s, e) => _imageBox.ZoomIn())             { DisplayStyle = ToolStripItemDisplayStyle.Text });
            _toolbar.Items.Add(new ToolStripButton("Zoom -",   null, (s, e) => _imageBox.ZoomOut())            { DisplayStyle = ToolStripItemDisplayStyle.Text });
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(new ToolStripButton("Rotar ↺",  null, (s, e) => Rotate(RotateFlipType.Rotate270FlipNone)) { DisplayStyle = ToolStripItemDisplayStyle.Text });
            _toolbar.Items.Add(new ToolStripButton("Rotar ↻",  null, (s, e) => Rotate(RotateFlipType.Rotate90FlipNone))  { DisplayStyle = ToolStripItemDisplayStyle.Text });
            _toolbar.Items.Add(new ToolStripButton("Voltear",  null, (s, e) => Rotate(RotateFlipType.Rotate180FlipNone)) { DisplayStyle = ToolStripItemDisplayStyle.Text });
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(new ToolStripButton("Recortar selección", null, (s, e) => OnCrop())            { DisplayStyle = ToolStripItemDisplayStyle.Text });
            _toolbar.Items.Add(new ToolStripButton("Guardar como…",      null, (s, e) => SaveAs())            { DisplayStyle = ToolStripItemDisplayStyle.Text });

            _imageBox = new ImageBox
            {
                Dock = DockStyle.Fill,
                SizeMode = ImageBoxSizeMode.Fit,
                AllowZoom = true,
                GridDisplayMode = ImageBoxGridDisplayMode.Client,
                SelectionMode = ImageBoxSelectionMode.Rectangle,
                BackColor = Color.FromArgb(64, 64, 64)
            };
            _imageBox.MouseMove += OnMouseMove;
            _imageBox.ZoomChanged += (s, e) => UpdateZoom();

            _statusBar = new StatusStrip();
            _lblPosition = new ToolStripStatusLabel("x=-, y=-");
            _lblZoom = new ToolStripStatusLabel("Zoom: 100%");
            _lblSize = new ToolStripStatusLabel("Sin imagen") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
            _statusBar.Items.Add(_lblPosition);
            _statusBar.Items.Add(new ToolStripSeparator());
            _statusBar.Items.Add(_lblZoom);
            _statusBar.Items.Add(new ToolStripSeparator());
            _statusBar.Items.Add(_lblSize);

            Controls.Add(_imageBox);
            Controls.Add(_statusBar);
            Controls.Add(_toolbar);
        }

        public void Display(Bitmap bmp)
        {
            // Liberamos imagen previa porque ImageBox solo guarda referencia
            var prev = _imageBox.Image;
            _imageBox.Image = bmp;
            if (prev != null && !ReferenceEquals(prev, bmp)) prev.Dispose();

            if (bmp != null)
            {
                _imageBox.ZoomToFit();
                _lblSize.Text = $"{bmp.Width}×{bmp.Height} px";

                // Pixel grid visible cuando zoom alto
                _imageBox.ShowPixelGrid = true;
            }
            else
            {
                _lblSize.Text = "Sin imagen";
            }
        }

        private void Rotate(RotateFlipType type)
        {
            if (_imageBox.Image is Bitmap b)
            {
                b.RotateFlip(type);
                _imageBox.Invalidate();
                _lblSize.Text = $"{b.Width}×{b.Height} px (rotada)";
            }
        }

        private void OnCrop()
        {
            if (_imageBox.Image == null || _imageBox.SelectionRegion.IsEmpty) return;
            try
            {
                var crop = _imageBox.GetSelectedImage();
                CropRequested?.Invoke(this, crop);
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo recortar: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SaveAs()
        {
            if (_imageBox.Image == null) return;
            using (var dlg = new SaveFileDialog
            {
                Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|TIFF (*.tif)|*.tif|BMP (*.bmp)|*.bmp",
                FileName = "page.png"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    _imageBox.Image.Save(dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error guardando: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var p = _imageBox.PointToImage(e.Location);
            _lblPosition.Text = $"x={p.X}, y={p.Y}";
        }

        private void UpdateZoom()
        {
            _lblZoom.Text = $"Zoom: {_imageBox.Zoom}%";
        }
    }
}
