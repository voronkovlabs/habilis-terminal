using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.Tests;

public class OfficeFilesTests
{
    [Theory]
    [InlineData(@"C:\a\report.docx", OfficeApp.Word)]
    [InlineData("OLD.DOC", OfficeApp.Word)]
    [InlineData("book.xlsm", OfficeApp.Excel)]
    [InlineData("slides.PPTX", OfficeApp.PowerPoint)]
    public void Office_files_pick_their_application(string path, OfficeApp app) => Assert.Equal(app, OfficeFiles.AppFor(path));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("data.csv")]
    [InlineData("docx")]
    public void Other_files_stay_with_the_usual_viewer(string path) => Assert.Null(OfficeFiles.AppFor(path));

    [Theory]
    [InlineData("a.docx", true)]
    [InlineData("a.XLSX", true)]
    [InlineData("a.pptx", true)]
    [InlineData("a.doc", false)]
    [InlineData("a.xlsm", false)]
    public void Only_current_formats_are_created(string path, bool create) => Assert.Equal(create, OfficeFiles.CanCreate(path));
}
