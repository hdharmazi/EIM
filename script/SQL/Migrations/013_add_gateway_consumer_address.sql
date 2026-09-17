-------------------------------------------------------------
-- 013 Record the gateway address each consumer calls.
--
-- Until now the gateway a key was checked against came only from the dashboard's
-- own configuration, so pointing a consumer at a different function app meant an
-- appsettings change and a redeploy. That put a routine onboarding step behind a
-- deployment, which is exactly what the consumer console exists to avoid.
--
-- GATEWAYURL is the base address that consumer's traffic goes to. It is the
-- address the dashboard verifies the consumer's key against, and the address an
-- administrator hands to the consumer. Null means "use the gateway configured for
-- this dashboard", which is what every existing row does.
--
-- The address is an operational detail, not a secret and not a credential. The
-- runtime authorization path in the gateway itself does not read this column: a
-- request is already at a gateway by the time it is authorized, so an address
-- stored here can never widen what a consumer is allowed to do.
-------------------------------------------------------------

IF COL_LENGTH('dbo.GATEWAYCONSUMER', 'GATEWAYURL') IS NULL
BEGIN
    ALTER TABLE [dbo].[GATEWAYCONSUMER]
        ADD [GATEWAYURL] NVARCHAR(200) NULL;
END
GO
