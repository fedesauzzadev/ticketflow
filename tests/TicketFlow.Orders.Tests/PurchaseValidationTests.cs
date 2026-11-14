using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Domain;

namespace TicketFlow.Orders.Tests;

/// <summary>Validación de la frontera HTTP del POST /purchases (NFR-1) — sin red.</summary>
public sealed class PurchaseValidationTests
{
    private static CardRequest Card(string? pan = "4111111111111111", string? holder = "Michael Jackson",
        string? expiry = "12/29", string? cvv = "123") =>
        new(pan, holder, expiry, cvv);

    [Fact]
    public void Valid_request_produces_no_errors()
    {
        var request = new PurchaseRequest("evt-mj-tribute-night", 2, Card());
        Assert.Empty(PurchaseRequestValidator.Validate(request));
    }

    [Theory]
    [InlineData(null, 0)]                       // sin evento
    [InlineData("Evt_Invalido", 1)]             // formato no kebab-case
    [InlineData("evt-ok", 0)]                   // seats fuera de rango (0)
    [InlineData("evt-ok", 9)]                   // seats fuera de rango (9)
    [InlineData("evt-ok", null)]                // seats ausente
    public void Bad_event_or_seats_are_rejected(string? eventId, int? seats)
    {
        var errors = PurchaseRequestValidator.Validate(new PurchaseRequest(eventId, seats, Card()));
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("411111111111")]        // 12 dígitos: muy corta
    [InlineData("41111111111111111199")] // 20 dígitos: muy larga
    [InlineData("411111111111111a")]    // no numérica
    [InlineData(null)]
    public void Bad_pan_is_rejected(string? pan)
    {
        var errors = PurchaseRequestValidator.Validate(new PurchaseRequest("evt-ok", 1, Card(pan: pan)));
        Assert.Contains(errors, e => e.Contains("pan"));
    }

    [Theory]
    [InlineData("13/29")] // mes inválido
    [InlineData("1229")]  // sin barra
    [InlineData("12/2029")] // año de 4 dígitos
    [InlineData(null)]
    public void Bad_expiry_is_rejected(string? expiry)
    {
        var errors = PurchaseRequestValidator.Validate(new PurchaseRequest("evt-ok", 1, Card(expiry: expiry)));
        Assert.Contains(errors, e => e.Contains("expiry"));
    }

    [Theory]
    [InlineData("12")]     // cvv corto
    [InlineData("1234")]   // cvv largo
    [InlineData("12a")]    // no numérico
    [InlineData(null)]
    public void Bad_cvv_is_rejected(string? cvv)
    {
        var errors = PurchaseRequestValidator.Validate(new PurchaseRequest("evt-ok", 1, Card(cvv: cvv)));
        Assert.Contains(errors, e => e.Contains("cvv"));
    }

    [Fact]
    public void Missing_card_is_rejected()
    {
        var errors = PurchaseRequestValidator.Validate(new PurchaseRequest("evt-ok", 1, null));
        Assert.Contains(errors, e => e.Contains("card es requerido"));
    }

    [Fact]
    public void Holder_must_have_2_to_80_chars()
    {
        var shortHolder = PurchaseRequestValidator.Validate(new PurchaseRequest("evt-ok", 1, Card(holder: "M")));
        Assert.Contains(shortHolder, e => e.Contains("holder"));
    }

    [Theory]
    [InlineData(null, "el header Idempotency-Key es requerido")]
    [InlineData("", "el header Idempotency-Key es requerido")]
    [InlineData("   ", "el header Idempotency-Key es requerido")]
    public void Idempotency_key_is_required(string? key, string expectedFragment)
    {
        Assert.Contains(expectedFragment, PurchaseRequestValidator.ValidateIdempotencyKey(key));
    }

    [Fact]
    public void Idempotency_key_longer_than_200_is_rejected()
    {
        Assert.NotNull(PurchaseRequestValidator.ValidateIdempotencyKey(new string('k', 201)));
        Assert.Null(PurchaseRequestValidator.ValidateIdempotencyKey(new string('k', 200)));
    }

    [Fact]
    public void Purchase_create_rejects_out_of_range_seats_and_bad_ids()
    {
        Assert.Throws<ArgumentException>(() =>
            Purchase.Create("pur-" + new string('a', 32), "evt-ok", 0, "4111111111111111", "k",
                DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            Purchase.Create(PurchaseIds.New(), "evt-ok", 9, "4111111111111111", "k", DateTimeOffset.UtcNow));
        Assert.False(PurchaseIds.IsValid("no-un-id"));
        Assert.True(PurchaseIds.IsValid(PurchaseIds.New()));
    }

    [Fact]
    public void Body_hash_is_stable_for_equal_requests_and_differs_on_any_change()
    {
        var a = new PurchaseRequest("evt-x", 1, new CardRequest("4111111111111111", "Mj", "12/29", "123"));
        var same = new PurchaseRequest("evt-x", 1, new CardRequest("4111111111111111", "Mj", "12/29", "123"));

        Assert.Equal(PurchaseBodyHash.Of(a), PurchaseBodyHash.Of(same)); // mismo body ⇒ mismo hash (replay)
        Assert.NotEqual(PurchaseBodyHash.Of(a), PurchaseBodyHash.Of(a with { Seats = 2 }));
        Assert.NotEqual(PurchaseBodyHash.Of(a),
            PurchaseBodyHash.Of(a with { Card = new CardRequest("5105105105100002", "Mj", "12/29", "123") }));
    }
}
