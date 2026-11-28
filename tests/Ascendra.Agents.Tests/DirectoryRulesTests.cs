using Ascendra.Agents.Domain;
using Ascendra.Agents.Infrastructure;
using ClosedXML.Excel;

namespace Ascendra.Agents.Tests;

public sealed class DirectoryRulesTests
{
    internal static AgentDirectoryRecord Person(string id = "A", string role = "Agente", bool active = true) =>
        new(id, "Nombre Compuesto", "Paterno", "Materno", "Alias", id.ToLowerInvariant(), role, active);

    [Fact]
    public void CanonicalNamePreservesAllPartsAndAlias()
    {
        var row = AgentDirectoryRules.Validate([Person() with { Id = " 12.0 ", Username = " USUÁRIO ", FirstName = " Nombre   Compuesto " }]).Single();
        Assert.Equal("12", row.Id);
        Assert.Equal("usuario", row.Username);
        Assert.Equal("Nombre Compuesto Paterno Materno", row.DisplayName);
        Assert.Contains("Alias", row.Aliases);
    }

    [Theory]
    [InlineData("Agente", false, false)]
    [InlineData("Supervisor", false, true)]
    [InlineData("Gerente", true, true)]
    [InlineData("Director", true, false)]
    public void PermissionsPreserveRoleBoundaries(string role, bool canManage, bool fullAccess)
    {
        var row = Person(role: role);
        Assert.Equal(canManage, AgentDirectoryRules.CanManage(row));
        Assert.Equal(fullAccess, row.ToUser().HasFullAccess);
        Assert.False(AgentDirectoryRules.CanManage(row with { IsActive = false }));
    }

    [Fact]
    public void HomonymsRemainSeparateAndInactiveHistoryIsKept()
    {
        var rows = AgentDirectoryRules.Validate([Person(), Person("B", active: false)]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].DisplayName, rows[1].DisplayName);
        Assert.NotEqual(rows[0].Id, rows[1].Id);
    }

    [Fact]
    public void RejectsDuplicateIdsAndNormalizedActiveUsers()
    {
        Assert.Throws<InvalidOperationException>(() => AgentDirectoryRules.Validate([Person("12"), Person("12.0")]));
        Assert.Throws<InvalidOperationException>(() => AgentDirectoryRules.Validate([Person(), Person("B") with { Username = " Á " }]));
    }

    [Fact]
    public void RejectsIncompleteIdentityUnknownRoleAndActivePlaceholder()
    {
        Assert.Throws<InvalidOperationException>(() => AgentDirectoryRules.Validate([Person() with { Id = "" }]));
        Assert.Throws<InvalidOperationException>(() => AgentDirectoryRules.Validate([Person() with { PaternalSurname = "" }]));
        Assert.Throws<InvalidOperationException>(() => AgentDirectoryRules.Validate([Person(role: "Administrador")]));
        Assert.Throws<InvalidOperationException>(() => AgentDirectoryRules.Validate([Person() with { Username = "N/A" }]));
    }

    [Fact]
    public void ReaderRejectsNonemptyRowsWithoutIdAndPreservesInactiveRoster()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ascendra-headcount-{Guid.NewGuid():N}.xlsx");
        try
        {
            WriteWorkbook(path);
            var import = new HeadcountImportReader().Read(path);
            Assert.Equal(2, import.Directory.Count);
            Assert.Single(import.Directory, row => row.IsActive);
            Assert.Equal(64, import.Fingerprint.Length);
            using var workbook = new XLWorkbook(path);
            workbook.Worksheet(1).Cell(3, 1).Value = "";
            workbook.Save();
            Assert.Throws<InvalidOperationException>(() => new HeadcountImportReader().Read(path));
        }
        finally { File.Delete(path); }
    }

    internal static void WriteWorkbook(string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Personas");
        string[] headers = ["id", "nombre", "a_paterno", "a_materno", "nombre_visible", "usuario", "estado", "rol"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        string[][] rows = [["M", "Nombre Compuesto", "Paterno", "Materno", "Alias", "m", "Activo", "Gerente"],
            ["I", "Histórico", "Paterno", "", "Alias histórico", "i", "Inactivo", "Agente"]];
        for (var r = 0; r < rows.Length; r++) for (var c = 0; c < headers.Length; c++) sheet.Cell(r + 2, c + 1).Value = rows[r][c];
        workbook.SaveAs(path);
    }
}
