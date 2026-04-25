namespace Fi6800Scanner.Core.Models
{
    public class ImprinterConfig
    {
        public bool Enabled { get; set; }
        public string Text { get; set; } = "";
        public int CounterStart { get; set; } = 1;
        public int CounterStep { get; set; } = 1;
        public int CounterDigits { get; set; } = 5;
    }
}
