using Rewind.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddRewind();

var app = builder.Build();

// Swagger is always enabled here since this is a sample/test API, not a production app.
app.UseSwagger();
app.UseSwaggerUI();

app.UseRewind();

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.MapPost("/boom", (BoomRequest request) =>
{
    throw new InvalidOperationException($"Simulated failure for order {request.OrderId}");
})
.WithName("Boom")
.WithOpenApi();

app.Run();

// Lets test projects reference Program (e.g. WebApplicationFactory<Program> in tests from `rewind generate-test`).
public partial class Program { }

record BoomRequest(string OrderId, string CardNumber, string Password);

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
