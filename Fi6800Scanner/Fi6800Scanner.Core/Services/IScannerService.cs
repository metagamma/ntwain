using System;
using System.Collections.Generic;
using Fi6800Scanner.Core.Models;

namespace Fi6800Scanner.Core.Services
{
    public interface IScannerService : IDisposable
    {
        bool IsOpen { get; }
        string CurrentSourceName { get; }

        IList<string> ListSources();
        Fi6800CapabilitySnapshot OpenAndProbe(IntPtr hwnd, string sourceNameContains = null);
        void Close();

        ProfileApplyResult ApplyProfile(ScanProfile profile);
        void StartScan();
        void CancelScan();

        event EventHandler<ScannedPage> PageReceived;
        event EventHandler<BatchProgress> ProgressChanged;
        event EventHandler<ScanErrorInfo> ErrorOccurred;
        event EventHandler<BatchSummary> BatchCompleted;
    }

    public class ProfileApplyResult
    {
        public bool Success { get; set; }
        public IList<string> Applied { get; set; }
        public IList<string> Skipped { get; set; }
        public IList<string> Failed { get; set; }
    }

    public class BatchProgress
    {
        public int PagesScanned { get; set; }
        public int PendingTransfers { get; set; }
        public PageSide LastSide { get; set; }
    }

    public class BatchSummary
    {
        public int TotalPages { get; set; }
        public TimeSpan Duration { get; set; }
        public int Errors { get; set; }
    }

    public class ScanErrorInfo
    {
        public string Message { get; set; }
        public string ConditionCode { get; set; }
        public Exception Exception { get; set; }
        public bool IsFatal { get; set; }
    }
}
