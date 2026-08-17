namespace DragAndDropTexturing.LanguageHelpers
{
    public sealed class LanguageRequest
    {
        public LanguageEnum Language { get; set; }
        public LanguageEnum TextLanguage { get; set; }
        public string TranslationText { get; set; } = "";
    }
}
