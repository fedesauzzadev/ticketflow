namespace TicketFlow.Orders.Tests;

/// <summary>
/// Serializa las clases de test que bombean colas reales o levantan el host completo de
/// Órdenes in-proc (sus hosted services — outbox relay, worker de eventos, sweeper de holds —
/// compiten por las mismas colas/tablas si corren en paralelo; hallazgo S11, mismo family que
/// el flake de servicios vivos de S10). Los tests de fakes puros quedan fuera (paralelos).
/// </summary>
[CollectionDefinition("orders-saga")]
public sealed class OrdersSagaCollection;
