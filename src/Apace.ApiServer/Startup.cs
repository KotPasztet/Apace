using Asp.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Apace.ApiServer.Authentication;
using Apace.ApiServer.Utils;

namespace Apace.ApiServer;

public class Startup
{
    // masks JWT-style tokens in the auth debug log so it can be shared without leaking them
    private static readonly System.Text.RegularExpressions.Regex JwtRegex = new(@"eyJ[\w-]+\.[\w-]+\.[\w-]+");

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers()
            .ConfigureApplicationPartManager(manager =>
            {
                manager.FeatureProviders.Add(new InternalControllerFeatureProvider());
            });

        services.AddResponseCompression(options =>
        {
            options.Providers.Add<GzipCompressionProvider>();
        });

        services.AddResponseCaching();

        services.AddApiVersioning(config =>
        {
            config.DefaultApiVersion = new ApiVersion(1, 1);
            config.AssumeDefaultVersionWhenUnspecified = true;
            config.ReportApiVersions = true;
        });

        services.AddAuthentication("GenoaAuth")
            .AddScheme<AuthenticationSchemeOptions, GenoaAuthenticationHandler>("GenoaAuth", null);

        // the Genoa session tokens (SigninController <-> GenoaAuthenticationHandler) are data-protected,
        // the keys must persist or every restart would invalidate all issued session tokens.
        //
        // The keys live in the data directory (../data) — the persistent /app/data volume in
        // docker, the installation's data folder outside it — next to api_config.json.
        // The previous location (<cwd>/data/dataprotection-keys) was inside the container's
        // image layer in docker: every redeploy destroyed the key ring and, with it, all
        // issued 12h Genoa session tokens (GenoaAuth 401 "was forbidden" on every Earth API
        // call after an update). Any keys still found at the legacy location (non-docker
        // installs) are migrated so their sessions survive the move.
        string dataProtectionKeysPath = Path.Combine("..", "data", "dataprotection-keys");
        Directory.CreateDirectory(dataProtectionKeysPath);
        MigrateLegacyDataProtectionKeys(Path.Combine(Environment.CurrentDirectory, "data", "dataprotection-keys"), dataProtectionKeysPath);
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
            .SetApplicationName("ApiServer"); // must stay stable across image rebuilds; matches the assembly name (the historical default)

        services.AddDbContext<LiveDbContext>(options => options.UseSqlite(Configuration.GetConnectionString("LiveDBConnection")));
    }

    private static void MigrateLegacyDataProtectionKeys(string legacyPath, string targetPath)
    {
        try
        {
            if (!Directory.Exists(legacyPath) || Directory.EnumerateFiles(targetPath, "key-*.xml").Any())
            {
                return;
            }

            var keyFiles = Directory.EnumerateFiles(legacyPath, "key-*.xml").ToList();
            if (keyFiles.Count == 0)
            {
                return;
            }

            foreach (string file in keyFiles)
            {
                File.Copy(file, Path.Combine(targetPath, Path.GetFileName(file)), overwrite: false);
            }

            Log.Information($"Migrated {keyFiles.Count} data protection key file(s) from {Path.GetFullPath(legacyPath)} to {Path.GetFullPath(targetPath)}");
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not migrate data protection keys from {Path.GetFullPath(legacyPath)} to {Path.GetFullPath(targetPath)}: {ex.Message} " +
                        "(issued session tokens will not validate — clients re-sign in on their next login)");
        }
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
#pragma warning disable IDE0060 // Remove unused parameter
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        var forwardedHeadersOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };

        forwardedHeadersOptions.KnownIPNetworks.Clear();
        forwardedHeadersOptions.KnownProxies.Clear();

        app.UseForwardedHeaders(forwardedHeadersOptions);

        app.Use(async (context, next) =>
        {
            context.Items.Add("RequestStartedOn", DateTimeOffset.UtcNow);
            await next();
        });

        // Debug aid for reverse-engineering the iOS (Xal/SISU) login flow: set APACE_LOG_AUTH_BODIES=1
        // to log method, headers and body of requests to the auth/login endpoints. Bodies can contain
        // tokens, so this stays off unless explicitly enabled.
        if (Environment.GetEnvironmentVariable("APACE_LOG_AUTH_BODIES") == "1")
        {
            app.Use(async (context, next) =>
            {
                string path = context.Request.Path.Value ?? string.Empty;
                if (path.Contains("xboxlive.com", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("live.com", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("playfab", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("oauth", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("sisu", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("ppsecure", StringComparison.OrdinalIgnoreCase))
                {
                    context.Request.EnableBuffering();
                    string body;
                    using (var reader = new System.IO.StreamReader(context.Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                    {
                        body = await reader.ReadToEndAsync();
                    }
                    context.Request.Body.Position = 0;

                    if (body.Length > 8000)
                    {
                        body = body[..8000] + "...(truncated)";
                    }

                    // login form posts carry the user's username and password, never log those
                    if (context.Request.Method == "POST"
                        && (path.Contains("oauth20_authorize", StringComparison.OrdinalIgnoreCase)
                            || path.Contains("ppsecure", StringComparison.OrdinalIgnoreCase)))
                    {
                        body = $"(redacted, {body.Length} chars: may contain credentials)";
                    }

                    string headers = string.Join("; ", context.Request.Headers.Select(h => $"{h.Key}={h.Value}"));
                    Log.Information("AUTHDEBUG {Method} {Path}{Query} | Headers: {Headers} | Body: {Body}",
                        context.Request.Method, path, context.Request.QueryString,
                        JwtRegex.Replace(headers, "<jwt>"), JwtRegex.Replace(body, "<jwt>"));

                    // also log what the server answers (status, redirect target, body); gzip is switched off so the body is readable
                    context.Request.Headers.Remove("Accept-Encoding");
                    var originalResponseBody = context.Response.Body;
                    using var capturedResponse = new MemoryStream();
                    context.Response.Body = capturedResponse;
                    try
                    {
                        await next();
                    }
                    finally
                    {
                        capturedResponse.Position = 0;
                        string responseBody;
                        using (var responseReader = new System.IO.StreamReader(capturedResponse, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                        {
                            responseBody = await responseReader.ReadToEndAsync();
                        }

                        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            responseBody = $"(html page, {responseBody.Length} chars)";
                        }
                        else if (responseBody.Length > 8000)
                        {
                            responseBody = responseBody[..8000] + "...(truncated)";
                        }

                        Log.Information("AUTHDEBUG-RESPONSE {Method} {Path} -> {Status} | Location: {Location} | Body: {Body}",
                            context.Request.Method, path, context.Response.StatusCode,
                            JwtRegex.Replace(context.Response.Headers.Location.ToString(), "<jwt>"),
                            JwtRegex.Replace(responseBody, "<jwt>"));

                        capturedResponse.Position = 0;
                        await capturedResponse.CopyToAsync(originalResponseBody);
                        context.Response.Body = originalResponseBody;
                    }

                    return;
                }

                await next();
            });
        }

        app.UseSerilogRequestLogging(options =>
        {
            // Customize the message template
            options.MessageTemplate = "{RemoteIpAddress} {RequestMethod} {RequestScheme}://{RequestHost}{RequestPath}{RequestQuery} responded {StatusCode} in {Elapsed:0.0000} ms";

            // Emit debug-level events instead of the defaults
            options.GetLevel = (httpContext, elapsed, ex) => LogEventLevel.Verbose;

            // Attach additional properties to the request completion event
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
                diagnosticContext.Set("RemoteIpAddress", httpContext.Connection.RemoteIpAddress);
                diagnosticContext.Set("RequestQuery", httpContext.Request.QueryString);
            };
        });

        app.UseStaticFiles();
        
        //app.UseHttpsRedirection();

        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();

        //app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TransactionManager.MaximumTimeout });

        app.UseETagger();

        app.UseResponseCaching();
        app.UseResponseCompression();

        //app.UseSession();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });

        // Background mirror of every servable live-catalog piece into the volume
        // (see LivePersonaCatalog.BinaryWarmUp) — the upstream content CDN is
        // losing MCE binaries, so still-alive pieces must be captured before the
        // client asks and dead ones must drop from the offer up front. Idempotent:
        // Program.Main starts the same loop (one per process).
        app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted.Register(() =>
                Services.LivePersonaCatalog.EnsureBinaryWarmUpStarted(Log.Logger));
    }
}
