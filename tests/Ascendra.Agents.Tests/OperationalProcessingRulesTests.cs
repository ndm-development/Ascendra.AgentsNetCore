using Ascendra.Agents.Domain;

namespace Ascendra.Agents.Tests;

public sealed class OperationalProcessingRulesTests
{
    [Fact]
    public void PositivePaymentInsideWindowActivatesFirstEligiblePromiseOnce()
    {
        var result = Process(
            payments: [Payment(10, "001", new(2026, 8, 3), 100m)],
            promises: [Promise(1, "ASC-000001", "001", "10", "2026-08-01 09:00", new(2026, 8, 3)),
                       Promise(2, "ASC-000002", "001", "20", "2026-08-02 09:00", new(2026, 8, 4))],
            cutoff: new(2026, 8, 10));

        var payment = Assert.Single(result.Payments);
        Assert.Equal("Vigente", payment.AssignmentType); Assert.Equal(1, payment.DatabasePromiseId);
        Assert.Equal("Activa", result.Promises.Single(row => row.DatabasePromiseId == 1).Status);
        Assert.Equal("Incumplida", result.Promises.Single(row => row.DatabasePromiseId == 2).Status);
    }

    [Fact]
    public void ZeroAndNegativeEventsRemainOrganicAndCannotActivate()
    {
        var result = Process(
            payments: [Payment(10, "001", new(2026, 8, 3), 0m), Payment(11, "001", new(2026, 8, 4), -1m)],
            promises: [Promise(1, "ASC-000001", "001", "10", "2026-08-01 09:00", new(2026, 8, 3))],
            cutoff: new(2026, 8, 10));

        Assert.All(result.Payments, payment => Assert.Equal("Orgánico", payment.AssignmentType));
        Assert.Equal("Incumplida", Assert.Single(result.Promises).Status);
    }

    [Fact]
    public void LatePaymentUsesCreationIsoWeekWithoutActivatingPromise()
    {
        // Lunes y jueves pertenecen a la misma semana ISO; el jueves ya está
        // fuera de la ventana [lunes, miércoles).
        var result = Process(
            payments: [Payment(10, "001", new(2026, 8, 6), 50m)],
            promises: [Promise(1, "ASC-000001", "001", "10", "2026-08-03 08:00", new(2026, 8, 3))],
            cutoff: new(2026, 8, 10));

        Assert.Equal("Tardía", Assert.Single(result.Payments).AssignmentType);
        Assert.Equal("Incumplida", Assert.Single(result.Promises).Status);
    }

    [Fact]
    public void RecurringPromisesAreDuplicatedOrRobbedAndNeverReceivePayments()
    {
        var result = Process(
            payments: [],
            promises: [Promise(1, "ASC-000001", "001", "10", "2026-08-01 09:00", new(2026, 8, 5)),
                       Promise(2, "ASC-000002", "001", "10", "2026-08-02 09:00", new(2026, 8, 5)),
                       Promise(3, "ASC-000003", "001", "20", "2026-08-02 10:00", new(2026, 8, 5))],
            cutoff: new(2026, 8, 10));

        Assert.Equal("Duplicada", result.Promises.Single(row => row.DatabasePromiseId == 2).Status);
        Assert.Equal("Robada", result.Promises.Single(row => row.DatabasePromiseId == 3).Status);
    }

    [Fact]
    public void PaymentInOriginalWindowStopsItFromBlockingFuturePromises()
    {
        var result = Process(
            payments: [Payment(10, "001", new(2026, 8, 4), 50m)],
            promises: [Promise(1, "ASC-000001", "001", "10", "2026-08-01 09:00", new(2026, 8, 5)),
                       Promise(2, "ASC-000002", "001", "20", "2026-08-02 10:00", new(2026, 8, 5))],
            cutoff: new(2026, 8, 10));

        Assert.Equal("Activa", result.Promises.Single(row => row.DatabasePromiseId == 1).Status);
        Assert.Equal("Incumplida", result.Promises.Single(row => row.DatabasePromiseId == 2).Status);
    }

    [Fact]
    public void CurrentPortfolioIsTheOnlyClientNameSource()
    {
        var result = OperationalProcessingRules.Process(
            [Payment(10, "999", new(2026, 8, 3), 50m)],
            [Promise(1, "ASC-000001", "999", "10", "2026-08-01 09:00", new(2026, 8, 3))],
            [new("001", "Cliente vigente", "Producto", 1m, "", "", "", [])], [], [], [], Agents, new(2026, 8, 10));

        Assert.Equal(string.Empty, Assert.Single(result.Payments).ClientName);
        Assert.Equal(string.Empty, Assert.Single(result.Promises).ClientName);
    }

    [Fact]
    public void ReferencesNormalizePhoneAndAddOnlyPortfolioPhonesNotAlreadyReferenced()
    {
        var result = OperationalProcessingRules.Process([], [],
            [new("001", "Cliente", "Producto", 15m, "", "", "", ["521 55 1111 2222", "55 3333 4444"])],
            [new("001", "55 1111 2222", "Ana", "Hermana"), new("001", "5511112222", "Ana", "Hermana"),
             new("999", "55 5555 5555", "Fuera", "Amigo")], [], [], Agents, new(2026, 8, 10));

        Assert.Equal(3, result.Contacts.Count);
        Assert.Contains(result.Contacts, row => row.ClientId == "001" && row.Phone == "5511112222" && row.Source == "Referencia");
        Assert.Contains(result.Contacts, row => row.ClientId == "001" && row.Phone == "5533334444" && row.Source == "Cartera");
        Assert.Contains(result.Contacts, row => row.ClientId == "999" && row.Status == "Inactivo" && row.ClientName == string.Empty);
    }

    [Fact]
    public void AgentIdHasPriorityAndUnknownIdsFailInsteadOfFallingBackToName()
    {
        var promise = Promise(1, "ASC-000001", "001", "999", "2026-08-01 09:00", new(2026, 8, 3));
        Assert.Throws<InvalidOperationException>(() => OperationalProcessingRules.Process([], [promise], [], [], [], [], Agents, new(2026, 8, 10)));
    }

    [Fact]
    public void HistoricalContactsRemainWhenClientLeavesDailyPortfolioAndBecomeInactive()
    {
        var result = OperationalProcessingRules.Process([], [], [], [],
            [new("001", "521 55 1111 2222", "", "", "Cartera"),
             new("001", "5533334444", "Ana", "Hermana", "Referencia")], [], Agents, new(2026, 8, 10));

        Assert.Equal(2, result.Contacts.Count);
        Assert.All(result.Contacts, contact =>
        {
            Assert.Equal("Inactivo", contact.Status);
            Assert.Equal(string.Empty, contact.ClientName);
        });
        Assert.Contains(result.Contacts, contact => contact.Source == "Cartera" && contact.Phone == "5511112222");
        Assert.Contains(result.Contacts, contact => contact.Source == "Referencia" && contact.ReferenceName == "Ana");
    }

    [Fact]
    public void GeographyMasterEnrichesPortfolioAndPaymentWhenDailyPortfolioHasNoZone()
    {
        var result = OperationalProcessingRules.Process(
            [Payment(10, "001", new(2026, 8, 3), 50m) with { Zone = null }], [],
            [new("001", "Cliente", "Producto", 10m, "", "", "", [])], [], [],
            [new("001", "Zona Norte")], Agents, new(2026, 8, 10));

        Assert.Equal("Zona Norte", Assert.Single(result.Portfolio).Zone);
        Assert.Equal("Zona Norte", Assert.Single(result.Payments).Zone);
    }

    private static OperationalProcessingResult Process(IReadOnlyList<OperationalPayment> payments,
        IReadOnlyList<OperationalPromise> promises, DateOnly cutoff) =>
        OperationalProcessingRules.Process(payments, promises,
            [new("001", "Cliente vigente", "Producto", 15m, "Activo", "Viernes", "Norte", ["5511112222"])], [], [], [], Agents, cutoff);

    private static readonly IReadOnlyList<OperationalAgent> Agents = [new("10", "ALICIA LÓPEZ GARCÍA"), new("20", "BRUNO PÉREZ DÍAZ")];

    private static OperationalPayment Payment(long id, string client, DateOnly received, decimal recovery) =>
        new(id, client, received, recovery, 32, "Producto", "Concepto", "Plan", "Zona");

    private static OperationalPromise Promise(int id, string visibleId, string client, string agentId, string created, DateOnly promiseDate) =>
        new(id, visibleId, id, client, "5511112222", "Alias", agentId, promiseDate, 1m, 1m,
            "Parcial", 1, DateTime.Parse(created, System.Globalization.CultureInfo.InvariantCulture));
}
