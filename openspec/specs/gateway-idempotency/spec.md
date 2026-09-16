# gateway-idempotency Specification

## Purpose
TBD - created by archiving change add-d365fsc-passthrough-gateway. Update Purpose after archive.
## Requirements
### Requirement: Idempotency key required on POST
The gateway SHALL require an `Idempotency-Key` header on every POST request, across all resource
types, and SHALL reject requests that omit it.

#### Scenario: Key supplied
- **WHEN** a caller POSTs with an `Idempotency-Key` header
- **THEN** the request is recorded and forwarded

#### Scenario: Key omitted
- **WHEN** a caller POSTs without an `Idempotency-Key` header
- **THEN** the gateway returns 400 and no D365 call is made

#### Scenario: Applies to services and actions
- **WHEN** a caller POSTs to a custom service or a bound action without an `Idempotency-Key`
- **THEN** the gateway returns 400

### Requirement: Replay of a completed request
The gateway SHALL return the stored response without contacting D365 when a POST presents a
previously completed `Idempotency-Key` together with an identical request fingerprint.

#### Scenario: Retry after a lost response
- **WHEN** a caller retries a POST with the same key and the same body after the original response was lost
- **THEN** the gateway returns the originally stored status and body, and D365 is not called a second time

#### Scenario: No duplicate record created
- **WHEN** the original POST created a record in D365 and the retry is replayed
- **THEN** exactly one record exists in D365

### Requirement: Key scoped to the consumer
Idempotency keys SHALL be scoped to the consumer, so that identical keys from different consumers
are independent and no consumer can observe another's stored responses.

#### Scenario: Same key from two consumers
- **WHEN** two consumers each POST with the same `Idempotency-Key` value
- **THEN** both requests are processed independently and neither receives the other's response

### Requirement: Conflicting reuse of a key
A POST presenting a previously used `Idempotency-Key` with a different request fingerprint SHALL be
rejected rather than replayed.

#### Scenario: Key reused for a different payload
- **WHEN** a caller POSTs with a key already recorded against a different body
- **THEN** the gateway returns 409 and does not replay the earlier response

### Requirement: Concurrent requests with the same key
The gateway SHALL reject rather than forward a second concurrent request presenting a key for which
a request is already in progress.

#### Scenario: Two instances receive the same retry
- **WHEN** two requests with the same consumer and key are processed concurrently by different instances
- **THEN** exactly one is forwarded to D365 and the other returns 409

### Requirement: Idempotency key honoured but not required on PATCH
The gateway SHALL accept and honour an `Idempotency-Key` on PATCH requests but SHALL NOT require
one.

#### Scenario: PATCH without a key
- **WHEN** a caller PATCHes without an `Idempotency-Key`
- **THEN** the request is forwarded normally

#### Scenario: PATCH with a key replayed
- **WHEN** a caller retries a PATCH with a previously completed key and identical body
- **THEN** the stored response is returned and D365 is not called again

### Requirement: Idempotency record retention
Idempotency records SHALL expire after a configured retention period and SHALL be purged
automatically.

#### Scenario: Expired key behaves as new
- **WHEN** a caller presents a key whose record has passed its retention period
- **THEN** the request is treated as a new request and forwarded

#### Scenario: Records purged on schedule
- **WHEN** the purge runs
- **THEN** records past their expiry are removed and unexpired records are retained

