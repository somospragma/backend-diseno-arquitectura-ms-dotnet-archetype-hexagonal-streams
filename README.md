# Arquetipo .NET 10 — API hexagonal con streams

Microservicio de cuentas sobre .NET 10 (LTS) con arquitectura hexagonal y streams nativos:
`IAsyncEnumerable` de punta a punta y `System.Threading.Channels` para eventos con backpressure.
Sin dependencias reactivas externas. Para un CRUD clasico usar el arquetipo `dotnet-api-async`.

> Documentacion completa (ADRs, threat model, runbook) pendiente con la plantilla de Alejandria.

## Cuando usarlo

- Listados o exportaciones grandes: las filas se leen y serializan en streaming, sin cargar todo en memoria.
- Notificaciones en vivo a clientes (Server-Sent Events).
- Publicacion de eventos que no debe bloquear la solicitud HTTP.

## Flujo de eventos

```
UseCase -> FanOutEventPublisher -+-> SqsEventPublisher -> Channel acotado (Wait = backpressure) -> SqsDispatcher -> SQS (lotes de 10)
                                 +-> ChannelAccountEventHub -> un Channel por suscriptor (DropOldest) -> SSE
```

El buffer hacia SQS es en memoria: si se requiere entrega garantizada ante caidas del proceso,
aplicar el patron outbox transaccional.

## Estructura

```
src/
  domain/          Model, Ports, UseCases (sin frameworks)
  infrastructure/
    driven-adapters/  Persistence (EF Core streaming), Cache (Redis), Messaging (SQS + BackgroundService),
                      EventStream (Channels), CreditScore (HTTP + resiliencia)
    entry-points/     Api (Minimal APIs, RFC 7807, Server-Sent Events)
    helpers/          Correlation id, security headers, enmascaramiento
  application/     Composition root (Program.cs, fan-out de eventos)
tests/             Unitarios, integracion, arquitectura (ArchUnitNET)
```

## Comandos

```bash
dotnet build Pragma.Accounts.slnx                              # warnings como errores + NuGetAudit
dotnet test --solution Pragma.Accounts.slnx --coverlet         # tests + gate de cobertura 90%
dotnet format Pragma.Accounts.slnx --verify-no-changes         # estilo
dotnet tool restore && (cd tests/Pragma.Accounts.Tests && dotnet stryker)   # mutation testing (60%)
docker build -t pragma-accounts-streams .
```

Configuracion por variables de entorno: ver `.env.example`.

## Endpoints

| Metodo | Ruta | Descripcion |
|---|---|---|
| GET | `/api/v1/accounts?limit=20` | Listado en streaming (JSON array) |
| GET | `/api/v1/accounts/events?accountId=` | Eventos en vivo (`text/event-stream`) |
| GET | `/api/v1/accounts/{id}` | Detalle |
| POST | `/api/v1/accounts` | Crear |
| PUT | `/api/v1/accounts/{id}` | Reemplazar |
| PATCH | `/api/v1/accounts/{id}` | Actualizacion parcial |
| DELETE | `/api/v1/accounts/{id}` | Eliminar |
| QUERY | `/api/v1/accounts/search` | Busqueda con body, en streaming |
| GET | `/api/v1/accounts/{id}/credit-score` | Puntaje crediticio |
| GET | `/health/live`, `/health/ready` | Health checks |
