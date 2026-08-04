# user-management Specification

## Purpose

Provide an Admin-only screen for managing dashboard users (email, display name, role, active status), enforced server-side regardless of client-side navigation gating.

## Requirements

### Requirement: User list (Admin only)
The system SHALL provide a user management screen, accessible only to Admin users, sourced from `api/user`, showing Email, Display Name, Role, Active status, and Created date. Server-side authorization SHALL enforce access regardless of client-side navigation gating.

#### Scenario: Admin views users
- **WHEN** an Admin user opens the Users screen
- **THEN** the list shows all users with Email, Display Name, Role, Active, and Created

#### Scenario: Non-admin cannot access
- **WHEN** a non-Admin user attempts to access user endpoints
- **THEN** the server denies the request even if the UI entry were reached

### Requirement: Create user
The system SHALL allow creating a user with email, display name, role, and active status.

#### Scenario: Create succeeds
- **WHEN** an Admin submits a valid new user
- **THEN** the user is created and appears in the list

### Requirement: Edit user
The system SHALL allow editing an existing user's display name, role, and active status.

#### Scenario: Edit succeeds
- **WHEN** an Admin updates a user and saves
- **THEN** the change is persisted and reflected in the list

### Requirement: Delete user
The system SHALL allow removing a user with confirmation.

#### Scenario: Delete with confirmation
- **WHEN** an Admin confirms removing a user
- **THEN** the user is removed from the list
