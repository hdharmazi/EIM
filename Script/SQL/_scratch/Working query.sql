SELECT * FROM ENVIRONMENTTABLE

SELECT TOP 100 * FROM D
ORDER BY ID DESC


SELECT  * FROM MESSAGEQUEUETABLE 
ORDER BY ID DESC


SELECT  * FROM LOGEVENTS 
ORDER BY ID DESC


--DELETE  FROM ENVIRONMENTTABLE 
WHERE [SYSTEM] = 'D365FSC' AND ENVIRONMENT = 'CDB-PROD'


GO

DELETE FROM MESSAGEQUEUETABLE WHERE CREATEDDATETIME BETWEEN '0001-01-01 00:00:00' AND '2025-03-16 08:36:13'

---- SAMPLE
INSERT INTO ENVIRONMENTTABLE VALUES ('CAPTURE', 'DEV', 'Endpoint=sb://asb-eim-wtc-nonprod.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=4gzdDB8HYmPSui549y8z+o+zHtFySmtM9+ASbLQswiw=')

SELECT * FROM ENVIRONMENTTABLE

{
  "ConnectionString": "Endpoint=sb://asb-eim-wtc-nonprod.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=4gzdDB8HYmPSui549y8z+o+zHtFySmtM9+ASbLQswiw=",
  "Parameters": 
  [
    {
        "key": "Customer",
        "value": "bat-d365-customer"
    }
    ,
    {
        "key": "ContactPerson",
        "value": "bat-d365-contactperson"
    }
  ]
}