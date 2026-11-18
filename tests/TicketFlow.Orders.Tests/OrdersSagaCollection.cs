// F-V2-01 (QA V2): la tabla `purchases`, el outbox y las colas de la saga del emulador son
// COMPARTIDAS y persistentes. Las collections de este proyecto corrían en paralelo y los
// escritores concurrentes (sweeper hosted de las factories in-proc, sweep manual del e2e,
// fixtures de holds due del repo) se pisaban entre sí (repro QA 11-15 §2b). Se serializa TODO
// el assembly: un solo escritor activo por vez, y las clases nuevas quedan seguras solas.
// Los otros proyectos de test tocan recursos disjuntos (colas it-* únicas / tablas propias),
// así que su paralelismo entre proyectos no necesita cambios.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Serializa las clases de test que bombean colas reales o levantan el host completo de
/// Órdenes in-proc (sus hosted services — outbox relay, worker de eventos, sweeper de holds —
/// compiten por las mismas colas/tablas si corren en paralelo; hallazgo S11, mismo family que
/// el flake de servicios vivos de S10). Redundante con el atributo de assembly de arriba,
/// queda como documentación viva de qué clases tocan estado compartido.
/// </summary>
[CollectionDefinition("orders-saga")]
public sealed class OrdersSagaCollection;
