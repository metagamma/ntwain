using System;
using System.Drawing;
using System.Windows.Forms;
using Fi6800Scanner.Core.Models;

namespace Fi6800Scanner.App.Controls
{
    public class PageThumbnailListView : UserControl
    {
        private readonly ListView _list;
        private readonly ImageList _imageList;
        private int _imageKeySeq;

        public event EventHandler<ScannedPage> PageSelected;

        public PageThumbnailListView()
        {
            Dock = DockStyle.Fill;

            _imageList = new ImageList
            {
                ImageSize = new Size(140, 200),
                ColorDepth = ColorDepth.Depth32Bit
            };

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.LargeIcon,
                LargeImageList = _imageList,
                MultiSelect = false,
                FullRowSelect = true,
                HideSelection = false,
                Activation = ItemActivation.OneClick
            };
            _list.SelectedIndexChanged += OnSelectionChanged;

            Controls.Add(_list);
        }

        public void Add(ScannedPage page)
        {
            string key = "k" + (_imageKeySeq++);
            if (page.Thumbnail != null)
                _imageList.Images.Add(key, page.Thumbnail);

            string label = $"#{page.PageNumber}";
            if (page.Side == PageSide.Rear) label += " (rev)";
            if (page.DetectedPatch.HasValue) label += $" [{page.DetectedPatch.Value}]";
            if (page.Barcodes != null && page.Barcodes.Count > 0) label += $" 📊";

            var item = new ListViewItem(label)
            {
                ImageKey = key,
                Tag = page,
                ToolTipText = BuildToolTip(page)
            };
            _list.ShowItemToolTips = true;
            _list.Items.Add(item);
            item.EnsureVisible();
        }

        public void Clear()
        {
            foreach (ListViewItem it in _list.Items)
            {
                if (it.Tag is ScannedPage p) p.Dispose();
            }
            _list.Items.Clear();
            _imageList.Images.Clear();
            _imageKeySeq = 0;
        }

        public int Count => _list.Items.Count;

        private static string BuildToolTip(ScannedPage page)
        {
            return string.Format(
                "Página {0} ({1})\n{2}×{3} @ {4}×{5} DPI\n{6}\n{7}{8}",
                page.PageNumber,
                page.Side,
                page.WidthPx, page.HeightPx,
                page.DpiX, page.DpiY,
                page.PixelType,
                page.DetectedPatch.HasValue ? "Patch: " + page.DetectedPatch.Value + "\n" : "",
                page.Barcodes != null && page.Barcodes.Count > 0
                    ? "Barcodes: " + string.Join(", ", System.Linq.Enumerable.Select(page.Barcodes, b => b.Text))
                    : "");
        }

        private void OnSelectionChanged(object sender, EventArgs e)
        {
            if (_list.SelectedItems.Count == 0) return;
            if (_list.SelectedItems[0].Tag is ScannedPage page)
                PageSelected?.Invoke(this, page);
        }
    }
}
