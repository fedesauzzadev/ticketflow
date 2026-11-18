# QA V2 adversarial - evidencia cruda (2026-11-15, HEAD 171638d)

Auditor: QA adversarial NSDD (F6, entrega V2). 100% evidencia propia.
Logs completos: %TEMP%/opencode/qa-v2/*.log

## 1. Suite (emulador arriba, servicios abajo)

| Corrida | Comando | Resultado | Tiempo |
|---|---|---|---|
| suite1 | dot-source emulator-env + dotnet test TicketFlow.sln | exit 0. Orders 92/92 (36 s), Catalog Core 80.22% (umbral verde) | 70 s |
| suite2 | idem | exit 0. **186/186** (P8+T5+C64+Prov17+O92), 0 omitidos | 153 s |

## 2. ci.ps1 (N-3001)

| Corrida | Resultado | Detalle | Tiempo |
|---|---|---|---|
| ci1 (verde) | exit 0 | 5 etapas OK: preflight 0.9 / build 17.7 / tests 180.0 / infra-verify 8.0 / workflow-lint 0.5 | 208 s |
| ci2 (ROJO propio) | **exit 1, CULPABLE: tests** | test inyectado QaRedBreak.cs (throw) en Payments.Tests: build OK, tests FAIL, etapas posteriores NO corrieron. Revertido | 184 s |
| ci3 (-Demos) | **exit 1, CULPABLE: tests - FLAKE REAL (F-V2-01)** | fallo legit: PurchaseRepositoryTests.Scan_finds_only_due_holds | 101 s |

### 2b. Repro del flake (F-V2-01)

- orders-run1 (proyecto Orders solo): 92/92 exit 0, 66 s.
- orders-run2: **exit 1, 2 fallos**: Scan_finds_only_due_holds Expected:["pur-ef6e06c596f648c9a4370a5058ed1b56"] Actual:[]; SagaEndToEndTests.Hold_expires l.592 Assert.Equal(1, sweep) **Actual 2**.
- pair runs (solo esas 2 clases, -p:Threshold=0): run1 **exit 1** (Hold_expires FAIL), run2/run3 exit 0 (1/3).
- Mecanismo: sweep manual del e2e escanea la TABLA COMPARTIDA purchases y expira holds due de fixtures de OTROS tests en paralelo; el scan del repo test ve su due ya EXPIRED -> found=[].

## 3. workflow-lint semantica rota (F-V2-02)

- ci.yml line 35 run: pwsh -File scripts/ci.ps1 -> reemplazado por scripts/other-gate.ps1 (YAML valido, invocacion real drifted).
- validate-workflow.ps1 -> **exit 0, todas OK** (la invariante central pasa por la mencion en COMENTARIO line 7). Restaurado.