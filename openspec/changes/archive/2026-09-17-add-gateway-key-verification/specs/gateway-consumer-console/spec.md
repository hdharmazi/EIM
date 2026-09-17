### Requirement: Gateway address held against the consumer
A consumer SHALL carry the gateway base address its traffic uses, editable from the consumer screen
without a deployment, and a key SHALL be verified against that address. Where a consumer has no
address of its own, the dashboard SHALL fall back to a gateway chosen from a list held by the server,
and where more than one such gateway is configured the administrator SHALL choose which applies
rather than the dashboard choosing on their behalf.

The address SHALL be constrained by a server-held policy on scheme and host. The dashboard server
issues the verification request itself, so an unconstrained address would let this screen reach any
host the server can, and would let the submitter choose an answerer that never refuses a key.

#### Scenario: Address entered when a consumer is created
- **WHEN** an administrator creates or edits a consumer
- **THEN** a gateway address field is offered, and leaving it blank uses the gateway the dashboard is configured with

#### Scenario: Key verified against the consumer's own address
- **WHEN** a key is registered for a consumer that has its own gateway address
- **THEN** the key is checked against that address, and the choice from the configured list is not offered

#### Scenario: Address outside policy is refused on save
- **WHEN** an address is submitted that is not https, carries credentials, a query string or a fragment, or whose host is not permitted
- **THEN** the consumer is not saved and the administrator is told why, while the field is still in front of them

#### Scenario: Single gateway needs no decision
- **WHEN** a consumer has no address of its own and exactly one gateway is configured
- **THEN** it is selected automatically and shown, so the common case requires no choice

#### Scenario: Several gateways require an explicit choice
- **WHEN** a consumer has no address of its own, more than one gateway is configured, and none is chosen
- **THEN** the key is not stored and the administrator is asked to choose, because verifying against a guessed application would record a result they did not ask for

#### Scenario: Unknown gateway is refused
- **WHEN** a gateway name that the server does not hold is submitted
- **THEN** the key is not stored and no verification request is made

#### Scenario: Target shown before saving
- **WHEN** the key dialog is open
- **THEN** it shows the gateway the key will be checked against and that gateway's base address

## MODIFIED Requirements

### Requirement: Function key registered without being stored
The dashboard SHALL allow an administrator to register a consumer's function key so that identity
verification can be enabled without database access. The submitted value SHALL be hashed on the
server, only the hash SHALL be persisted, and the raw value SHALL NOT be stored, logged or returned
by any endpoint.

Before storing, the dashboard SHALL verify the key against the configured gateway application and
SHALL refuse to store a key that cannot be shown to be accepted by it. The verification
request SHALL omit the consumer identifier header so that it is refused by the gateway before any
consumer lookup, grant evaluation, or environment credential resolution occurs.

#### Scenario: Key registered
- **WHEN** an administrator submits a function key for a consumer and the gateway application accepts it
- **THEN** its hash is stored, the consumer is reported as verified, and the response contains no key material

#### Scenario: Key is never readable afterwards
- **WHEN** an administrator views a consumer after registering a key
- **THEN** the screen reports only whether identity is verified, and no endpoint returns the key or its hash

#### Scenario: A hash submitted in place of a key is rejected
- **WHEN** an administrator submits a value that is already a hexadecimal digest rather than a key
- **THEN** the request is refused, because storing the hash of a hash would silently never match

#### Scenario: A key no gateway issued is refused
- **WHEN** the gateway application rejects the submitted key as unauthorised
- **THEN** the key is not stored, the consumer's existing key hash is left unchanged, and the administrator is told the key was rejected

#### Scenario: Verification does not reach D365 or read credentials
- **WHEN** the dashboard verifies a key against a gateway application
- **THEN** no D365 request is made, no environment credential is read, and no idempotency record is written

#### Scenario: A responder that is not the gateway is never accepted
- **WHEN** the address answers but the response does not carry the gateway's correlation header
- **THEN** the key is not stored and the result is reported as unable to check, because every application under the same domain answers and "not refused" is not proof of a gateway

## ADDED Requirements

### Requirement: Gateway application recorded from verification
The dashboard SHALL record when a consumer's key was verified and against which gateway application,
determined by the verification attempt rather than supplied by the administrator. A consumer whose key
predates verification, or whose key was recorded without being checked, SHALL report its verification
state as unknown rather than assuming one.

#### Scenario: Accepting application recorded
- **WHEN** the gateway application accepts a submitted key
- **THEN** that application and the time of verification are stored against the consumer and shown in the consumer list

#### Scenario: Application not asserted by the administrator
- **WHEN** an administrator registers a key
- **THEN** no field is offered for declaring the gateway application, because a declared value could contradict the key

#### Scenario: Legacy registration reports unknown
- **WHEN** a consumer has a key hash recorded but no verification result
- **THEN** its verification state is shown as unknown and is visually distinct from a verified consumer

#### Scenario: Unchecked registration is not shown as verified
- **WHEN** a key was stored without being checked against a gateway application
- **THEN** the consumer is shown as recorded but unchecked, and never as verified

### Requirement: Unreachable gateway distinguished from rejected key
The dashboard SHALL distinguish a key that the gateway refused from a key that could not be checked
because the gateway was unreachable. Both SHALL refuse to store the key, and the administrator SHALL be
told which of the two occurred.

#### Scenario: Gateway not reachable
- **WHEN** the gateway application fails to respond or times out
- **THEN** the key is not stored and the administrator is told the key could not be checked, not that it was rejected

#### Scenario: Slow gateway is not a rejection
- **WHEN** the gateway application does not answer within the configured timeout
- **THEN** it counts as unreachable rather than as having rejected the key

#### Scenario: A loopback gateway records the key without verifying it
- **WHEN** the configured gateway address is a loopback address
- **THEN** the key is stored without being probed, and is recorded as unchecked rather than verified, because a local Functions host does not enforce keys and would accept any value

### Requirement: Verification reported as a point in time
The dashboard SHALL present key verification as having occurred at a recorded time rather than as a
continuing guarantee, because a key may be deleted in Azure after it is registered.

#### Scenario: Verification date shown
- **WHEN** an administrator views a consumer whose key was verified
- **THEN** the date of verification is shown alongside the verified state

#### Scenario: Key registration warns about replacement
- **WHEN** an administrator opens the key dialog for a consumer that already has a key registered
- **THEN** the dialog states that registering a different key will stop the current key being accepted
