namespace UnderMineApi.Models
{
    public class NewsPart
    {
        public string Type { get; set; } = "Run";
        public string? Text { get; set; }
        public bool IsBold { get; set; }
        public string? Color { get; set; }
        public int? FontSize { get; set; }
    }
}
