var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
using (var scope = app.Services.CreateScope()) { scope.ServiceProvider.GetRequiredService<ShopDb>().Database.Migrate(); }
app.MapHealthChecks("/healthz");
app.Run();
