using TicketFlow.Catalog.Core.Application;
using TicketFlow.Catalog.Core.Domain;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Tests unitarios de dominio y de la frontera de config SEED_EVENTS (NFR-1:
/// la entrada externa se valida con mensaje accionable).
/// </summary>
public sealed class DomainAndSeedTests
{
    private static readonly DateTimeOffset Date = new(2027, 3, 12, 21, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("evt-ok")]
    [InlineData("a")]
    [InlineData("evt-legacy-tour-2027")]
    public void Valid_event_ids_pass(string id) => Assert.True(CatalogEventId.IsValid(id));

    [Theory]
    [InlineData("")]
    [InlineData("Evt-Mayuscula")]
    [InlineData("-arranca-con-guion")]
    [InlineData("termina-guion-")]
    [InlineData("con_underscore")]
    [InlineData("con espacio")]
    [InlineData("demasiado-largo-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Invalid_event_ids_fail(string id) => Assert.False(CatalogEventId.IsValid(id));

    [Fact]
    public void Create_rejects_invalid_event()
    {
        Assert.Throws<ArgumentException>(() => CatalogEvent.Create("BAD", "Nombre", Date, 10m, 5));
        Assert.Throws<ArgumentException>(() => CatalogEvent.Create("evt-ok", "", Date, 10m, 5));
        Assert.Throws<ArgumentException>(() => CatalogEvent.Create("evt-ok", new string('x', 201), Date, 10m, 5));
        Assert.Throws<ArgumentException>(() => CatalogEvent.Create("evt-ok", "Nombre", default, 10m, 5));
        Assert.Throws<ArgumentException>(() => CatalogEvent.Create("evt-ok", "Nombre", Date, -1m, 5));
        Assert.Throws<ArgumentException>(() => CatalogEvent.Create("evt-ok", "Nombre", Date, 10m, -1));
    }

    [Fact]
    public void Seed_parses_valid_spec_with_invariant_culture()
    {
        var events = SeedEvents.Parse(
            "evt-a|Show A|2027-03-12T21:00:00Z|120.50|500;evt-b|Show B|2027-04-03T20:00:00Z|85|250");

        Assert.Equal(2, events.Count);
        Assert.Equal("evt-a", events[0].Id);
        Assert.Equal("Show A", events[0].Name);
        Assert.Equal(new DateTimeOffset(2027, 3, 12, 21, 0, 0, TimeSpan.Zero), events[0].Date);
        Assert.Equal(120.50m, events[0].Price);
        Assert.Equal(500, events[0].Available);
        Assert.Equal(250, events[1].Available);
    }

    [Theory]
    [InlineData("evt-a|Solo-cuatro-campos|2027-03-12T21:00:00Z|10")]
    [InlineData("evt-a|Show|fecha-no-iso|10|5")]
    [InlineData("evt-a|Show|2027-03-12T21:00:00Z|precio|5")]
    [InlineData("evt-a|Show|2027-03-12T21:00:00Z|10|muchos")]
    [InlineData(";;")]
    [InlineData("evt-a|Show|2027-03-12T21:00:00Z|10|5;evt-a|Show 2|2027-04-12T21:00:00Z|20|6")] // id duplicado
    public void Seed_rejects_invalid_spec(string spec) => Assert.Throws<ArgumentException>(() => SeedEvents.Parse(spec));

    [Fact]
    public void Seed_default_provides_at_least_two_valid_events()
    {
        var events = SeedEvents.Parse(null);
        Assert.True(events.Count >= 2);
        Assert.All(events, e => Assert.True(CatalogEventId.IsValid(e.Id)));
        Assert.Equal(events.Count, events.Select(e => e.Id).Distinct().Count());
    }
}
