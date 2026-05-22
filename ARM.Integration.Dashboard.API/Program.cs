using ARM.Integration.Dashboard.API.Middleware;
using ARM.Integration.Dashboard.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<MessageQueueRepository>();
builder.Services.AddScoped<EnvironmentRepository>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<SimulationService>();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthorization();
app.MapControllers();

app.Run();
