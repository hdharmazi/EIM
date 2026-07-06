using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;

namespace ARM.Integration.EIM.Utilities
{
    public class EIMUtilities
    {
        public static string GetEnvironment(string system = "", string environment = "", string connectionString = null)
        {
            var conn = connectionString ?? Environment.GetEnvironmentVariable("EIMDB");

            string sql = "SELECT * FROM ENVIRONMENTTABLE WHERE SYSTEM = @SYSTEM AND ENVIRONMENT = @ENVIRONMENT";

            string value = "";

            new SQLServerHelper(conn).ExecuteReader(sql, reader =>
            {
                value = reader["VALUE"].ToString();
            }, CommandType.Text,
                new SqlParameter("@SYSTEM", system),
                new SqlParameter("@ENVIRONMENT", environment));

            if (string.IsNullOrEmpty(value))
            {
                throw new ApplicationException($"No ENVIRONMENTTABLE record could be found for environment '{environment}' and SYSTEM '{system}'.");
            }

            return value;
        }

        public static string GetEnvironments(string system = "", string environment = "")
        {
            
            var conn = Environment.GetEnvironmentVariable("EIMDB");
            SQLServerHelper sqlhelper = new SQLServerHelper(conn);

            var systemvar = string.IsNullOrWhiteSpace(system) ? "" : $"{system}";
            var environmentvar = string.IsNullOrWhiteSpace(environment) ? "" : environment;

            var environmentcontracts = new List<EnvironmentContract>();

            //deduplicate the contractid and contractseq
            var sql = @"
        SELECT SYSTEM, ENVIRONMENT, VALUE 
        FROM ENVIRONMENTTABLE 
        WHERE 
            (@environmentvar IS NULL OR @environmentvar = '' OR ENVIRONMENT = @environmentvar)
            AND (@systemvar IS NULL OR @systemvar = '' OR SYSTEM = @systemvar)";         

            var sqlParameters = new Dictionary<string, object>
                {
                   { "@environmentvar", environmentvar },
                   { "@systemvar", systemvar }
                };

            sqlhelper.ExecuteReader(sql, reader =>
            {
                var environmentconract = new EnvironmentContract
                {
                    system = reader["SYSTEM"].ToString(),
                    environment = reader["ENVIRONMENT"].ToString(),
                    value = reader["VALUE"].ToString()
                };

                environmentcontracts.Add(environmentconract);
            }
            , sqlParameters);
           
            return JsonSerializer.Serialize(environmentcontracts);
        }

        public static string GetMessages(string id)
        {
            var conn = Environment.GetEnvironmentVariable("EIMDB");

            string sql = "SELECT * FROM MESSAGEQUEUETABLE WHERE ID = @ID";

            QueueMessageContract messagesContract = new QueueMessageContract();
            string json = "";

            new SQLServerHelper(conn).ExecuteReader(sql, reader =>
            {
                messagesContract.Id = reader["ID"].ToString();
                messagesContract.SourceId = reader["SOURCEID"].ToString();
                messagesContract.SourceSystem = reader["SOURCESYSTEM"].ToString();
                messagesContract.SourceEntity = reader["SOURCEENTITY"].ToString();
                messagesContract.DestinationSystem = reader["DESTINATIONSYSTEM"].ToString();
                messagesContract.DestinationEntity = reader["DESTINATIONENTITY"].ToString();
                messagesContract.Payload = reader["PAYLOAD"].ToString();
                messagesContract.Type = reader["TYPE"].ToString();
                messagesContract.Environment = reader["ENVIRONMENT"].ToString();
                json = JsonSerializer.Serialize(messagesContract);
            }, CommandType.Text, new SqlParameter("@ID", id));

            if (string.IsNullOrEmpty(json))
                throw new ApplicationException($"No MESSAGEQUEUETABLE record could be found for ID '{id}'.");

            return json;
        }

        public static string InsertMessages(string messages)
        {
            var conn = Environment.GetEnvironmentVariable("EIMDB");
            var data = JsonSerializer.Deserialize<QueueMessageContract>(messages);

            const string sqlinsert = @"
                DECLARE @Out TABLE (ID BIGINT);
                INSERT INTO MESSAGEQUEUETABLE 
                    (ENVIRONMENT, SOURCEID, SOURCESYSTEM, SOURCEENTITY, DESTINATIONSYSTEM, DESTINATIONENTITY, 
                     TYPE, PAYLOAD, STATUS, WORKFLOWID, ACTIONID, REPONSEQUEUE)
                OUTPUT INSERTED.ID INTO @Out
                VALUES 
                    (@Environment, @SourceId, @SourceSystem, @SourceEntity, @DestinationSystem, @DestinationEntity,
                     @Type, @Payload, @Status, @WorkflowId, @ActionId, @ResponseQueue);
                SELECT ID FROM @Out;";

            var parameters = new SqlParameter[]
            {
                new("@Environment", data.Environment ?? (object)DBNull.Value),
                new("@SourceId", data.SourceId ?? (object)DBNull.Value),
                new("@SourceSystem", data.SourceSystem ?? (object)DBNull.Value),
                new("@SourceEntity", data.SourceEntity ?? (object)DBNull.Value),
                new("@DestinationSystem", data.DestinationSystem ?? (object)DBNull.Value),
                new("@DestinationEntity", data.DestinationEntity ?? (object)DBNull.Value),
                new("@Type", data.Type ?? (object)DBNull.Value),
                new("@Payload", data.Payload ?? (object)DBNull.Value),
                new("@Status", "NEW"),
                new("@WorkflowId", data.WorkflowId ?? (object)DBNull.Value),
                new("@ActionId", data.ActionId ?? (object)DBNull.Value),
                new("@ResponseQueue", data.ResponseQueue ?? (object)DBNull.Value),
            };

            new SQLServerHelper(conn).ExecuteReader(sqlinsert, reader =>
            {
                data.Id = reader["ID"].ToString();
            }, CommandType.Text, parameters);

            return data.Id;
        }

        public static int UpsertEnvironment(string messages)
        {
            var conn = Environment.GetEnvironmentVariable("EIMDB");

            var data = JsonSerializer.Deserialize<EnvironmentContract>(messages);
            var xvalues = "";

            try
            {
                // Try to parse value as JSON, if not, use as string
                using (JsonDocument doc = JsonDocument.Parse(data.value))
                {
                    xvalues = doc.RootElement.ToString();
                }
            }
            catch
            {
                xvalues = data.value;
            }

            const string sql = @"
                MERGE INTO ENVIRONMENTTABLE AS old
                USING (VALUES (@System, @Environment, @Value)) AS new (SYSTEM, ENVIRONMENT, VALUE)
                ON new.SYSTEM = old.SYSTEM AND new.ENVIRONMENT = old.ENVIRONMENT
                WHEN MATCHED THEN UPDATE SET old.VALUE = new.VALUE
                WHEN NOT MATCHED BY TARGET THEN INSERT (SYSTEM, ENVIRONMENT, VALUE) VALUES (SYSTEM, ENVIRONMENT, VALUE);";

            var parameters = new SqlParameter[]
            {
                new("@System", data.system ?? (object)DBNull.Value),
                new("@Environment", data.environment ?? (object)DBNull.Value),
                new("@Value", xvalues ?? (object)DBNull.Value),
            };

            int result = new SQLServerHelper(conn).ExecuteNonQuery(sql, CommandType.Text, parameters);

            return result;
        }

        public static int UpdateMessageLog(string id, string message, int status)
        {
            var conn = Environment.GetEnvironmentVariable("EIMDB");

            const string sql = "UPDATE MESSAGEQUEUETABLE SET STATUS = @Status, MESSAGELOG = @Message WHERE ID = @Id";

            var parameters = new SqlParameter[]
            {
                new("@Status", status),
                new("@Message", message ?? (object)DBNull.Value),
                new("@Id", id),
            };

            int result = new SQLServerHelper(conn).ExecuteNonQuery(sql, CommandType.Text, parameters);

            return result;
        }
     
        //Delete messages by date
        public static int DeleteMessages(string datefrom, string dateto)
        {
            var conn = Environment.GetEnvironmentVariable("EIMDB");

            const string sql = "DELETE FROM MESSAGEQUEUETABLE WHERE CREATEDDATETIME BETWEEN @DateFrom AND @DateTo";

            var parameters = new SqlParameter[]
            {
                new("@DateFrom", datefrom),
                new("@DateTo", dateto),
            };

            int result = new SQLServerHelper(conn).ExecuteNonQuery(sql, CommandType.Text, parameters);

            return result;
        }

        //Delete logevents by date
        public static int DeleteLogEvents(string datefrom, string dateto)
        {
            var conn = Environment.GetEnvironmentVariable("EIMDB");

            const string sql = "DELETE FROM LOGEVENTS WHERE TIMESTAMP BETWEEN @DateFrom AND @DateTo";

            var parameters = new SqlParameter[]
            {
                new("@DateFrom", datefrom),
                new("@DateTo", dateto),
            };

            int result = new SQLServerHelper(conn).ExecuteNonQuery(sql, CommandType.Text, parameters);

            return result;
        }

        public static List<string> LookupTargetField(string sourceField, string sourceSystem, string environment, string type)
        {
            var connectionString = Environment.GetEnvironmentVariable("EIMDB");

            const string sql = @"SELECT TARGETFIELD FROM FIELDMAPPINGTABLE 
                WHERE ENVIRONMENT = @Environment AND SOURCESYSTEM = @SourceSystem 
                AND SOURCEFIELD = @SourceField AND TYPE = @Type";

            var parameters = new SqlParameter[]
            {
                new("@Environment", environment),
                new("@SourceSystem", sourceSystem),
                new("@SourceField", sourceField),
                new("@Type", type),
            };

            List<string> records = new List<string>();

            new SQLServerHelper(connectionString).ExecuteReader(sql, reader =>
            {
                records.Add(reader[0].ToString());
            }, CommandType.Text, parameters);

            return records;
        }
    }
}