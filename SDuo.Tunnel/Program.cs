using Aliyun.OSS;
using Hestia.Core;
using Hestia.Tunnel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SDuo.Tunnel
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var factory = LoggerFactory.Create(logging => {
                logging.AddConsole();
            });
            var logger = factory.CreateLogger(typeof(Program));

            Utility.RegisterProvider();

            var builder = WebApplication.CreateBuilder(args);            

            builder.Host.ConfigureAppConfiguration((host, configuration) => {
                var local = builder.Configuration.GetValue("local", "yarp.json");
                if (!Path.IsPathRooted(local)){ local = Path.Combine(Environment.CurrentDirectory, local);}
                logger.LogInformation($"Local Json: {local}");
                if (!File.Exists(local)) { return; }
                logger.LogInformation($"Load Json: {local}");
                try
                {
                    configuration.AddJsonFile(local, true, true);
                }
                catch
                {
                    File.Delete(local);
                }                
            });            

            builder.Host.ConfigureServices((host, services) =>
            {
                while (true)
                {
                    string endpoint = builder.Configuration.GetValue<string>("Endpoint", null);
                    if (string.IsNullOrEmpty(endpoint)){break;}
                    logger.LogInformation($"OSS Endpoint: {endpoint}");
                    string ak = builder.Configuration.GetValue<string>("AK", null);
                    if (string.IsNullOrEmpty(ak)) { break; }
                    logger.LogInformation($"OSS AK: {ak}");
                    string sk = builder.Configuration.GetValue<string>("SK", null);
                    if (string.IsNullOrEmpty(sk)) { break; }
                    logger.LogInformation($"OSS SK: {string.Concat(sk.Select(x=>'*'))}");
                    services.AddSingleton<IOss>((sp) => {
                        return new OssClient(endpoint, ak, sk);
                    });
                    break;
                }
                var yarp = builder.Configuration.GetSection("YARP");
                services.AddTunnel(yarp);
            });

            builder.WebHost.UseContentRoot(Environment.CurrentDirectory);

            builder.WebHost.ConfigureKestrel((kestrel) => {
                logger.LogInformation($"Platform: {Environment.OSVersion}");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {return;}
                var socket = builder.Configuration.GetValue<string>("Socket", null);
                if (string.IsNullOrEmpty(socket)) { return; }                
                if ( File.Exists(socket)) {
                    logger.LogInformation($"Remove Socket: {socket}");
                    File.Delete(socket); 
                }
                logger.LogInformation($"Listen Socket: {socket}");
                kestrel.ListenUnixSocket(socket);
            });

            var app = builder.Build();

            var reload = builder.Configuration.GetValue("api:reload", "/api/reload");
            logger.LogInformation($"Reload: {reload}");

            app.MapPost(reload, async (context) => {
                IConfiguration configuration = context.RequestServices.GetRequiredService<IConfiguration>();                
                var logger = context.RequestServices.GetService<ILogger<Program>>();

                string token = configuration.GetValue<string>("Token", null);
                if (!string.IsNullOrEmpty(token))
                {
                    if (!context.Request.HasFormContentType) {
                        context.Response.StatusCode = 404;
                        return;
                    }
                    logger.LogInformation($"Token: {string.Concat(token.Select(x => '*'))}");
                    string ticket = context.Request.Form["Ticket"].At();
                    logger?.LogInformation($"Ticket: {ticket}");
                    if (!string.Equals(token, ticket, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }
                }

                while (true)
                {
                    string bucket = configuration.GetValue<string>("Bucket", null);
                    if (string.IsNullOrEmpty(bucket)) { break; }
                    logger?.LogInformation($"Bucket: {bucket}");
                    string remote = configuration.GetValue<string>("Remote",null);
                    if (string.IsNullOrEmpty(remote)) { break; }
                    logger?.LogInformation($"Remote Json: {remote}");
                    IOss oss = context.RequestServices.GetRequiredService<IOss>();
                    if (!oss.DoesObjectExist(bucket, remote)) { break; }
                    var result = oss.GetObject(bucket, remote);
                    var local = builder.Configuration.GetValue("Local", "yarp.json");
                    if (!Path.IsPathRooted(local)) { local = Path.Combine(Environment.CurrentDirectory, local); }
                    logger?.LogInformation($"Local Json: {local}");

                    using var fs = new FileStream(local, FileMode.OpenOrCreate, FileAccess.Write);
                    await result.ResponseStream.CopyToAsync(fs, context.RequestAborted);

                    if (!File.Exists(local)) { break; }
                    logger?.LogInformation($"Save Json: {local}");                   

                    break;
                }                

                await context.Response.WriteAsync($"{configuration.GetSection("YARP").GetValue("Version","UNKOWN")}", context.RequestAborted);

                bool exit = context.Request.Form["Exit"].At()?.ToBoolean() ?? false;
                if (exit)
                {
                    IHostApplicationLifetime lf = context.RequestServices.GetService<IHostApplicationLifetime>();
                    logger?.LogInformation($"Exit: {exit}");
                    lf?.StopApplication();
                }
            });

            app.UseTunnel();

            app.Run();
        }
    }
}





