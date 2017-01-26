using System;
using System.IO;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.ServiceProcess;
using System.Configuration.Install;
using System.ComponentModel;
using System.Xml;
using System.Xml.Serialization;

namespace OruxPals
{
    class Program
    {
        static void Main(string[] args)
        {
            if (RunConsole(args, OruxPalsServer.serviceName))
            {
                OruxPals.OruxPalsServer ops = new OruxPals.OruxPalsServer();
                ops.Start();
                Console.ReadLine();
                ops.Stop();
                return;
            };
        }

        static bool RunConsole(string[] args, string ServiceName)
        {
            if (!Environment.UserInteractive) // As Service
            {
                using (OruxPalsServerSvc service = new OruxPalsServerSvc()) ServiceBase.Run(service);
                return false;
            };

            if ((args == null) || (args.Length == 0))
                return true;

            switch (args[0])
            {
                case "-i":
                case "/i":
                case "-install":
                case "/install":
                    OruxPalsServerSvc.Install(false, args, ServiceName);
                    return false;
                case "-u":
                case "/u":
                case "-uninstall":
                case "/uninstall":
                    OruxPalsServerSvc.Install(true, args, ServiceName);
                    return false;
                case "-start":
                case "/start":
                    {
                        Console.WriteLine("Starting service {0}...", ServiceName);
                        ServiceController service = new ServiceController(ServiceName);
                        service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                        Console.WriteLine("Service {0} is {1}", ServiceName, service.Status.ToString());
                        return false;
                    };
                case "-stop":
                case "/stop":
                    {
                        Console.WriteLine("Starting service {0}...", ServiceName);
                        ServiceController service = new ServiceController(ServiceName);
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                        Console.WriteLine("Service {0} is {1}", ServiceName, service.Status.ToString());
                        return false;
                    };
                case "-restart":
                case "/restart":
                    {
                        Console.WriteLine("Starting service {0}...", ServiceName);
                        ServiceController service = new ServiceController(ServiceName);
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                        Console.WriteLine("Service {0} is {1}", ServiceName, service.Status.ToString());
                        service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                        Console.WriteLine("Service {0} is {1}", ServiceName, service.Status.ToString());
                        return false;
                    };
                case "-status":
                case "/status":
                    {
                        ServiceController service = new ServiceController(ServiceName);
                        Console.WriteLine("Service {0} is {1}", ServiceName, service.Status.ToString());
                        return false;
                    };
                default:
                    Console.WriteLine(args[0]+":"+Buddie.Hash(args[0].ToUpper()));
                    System.Threading.Thread.Sleep(1000);
                    return false;
            };
        }
    }

    public class OruxPalsServerSvc : ServiceBase
    {
        public OruxPalsServer ops = new OruxPalsServer();

        public OruxPalsServerSvc()
        {
            ServiceName = OruxPalsServer.serviceName;
        }

        protected override void OnStart(string[] args)
        {
            ops.Start();
        }

        protected override void OnStop()
        {
            ops.Stop();
        }

        public static void Install(bool undo, string[] args, string ServiceName)
        {
            try
            {
                Console.WriteLine(undo ? "Uninstalling service {0}..." : "Installing service {0}...", ServiceName);
                using (AssemblyInstaller inst = new AssemblyInstaller(typeof(Program).Assembly, args))
                {
                    IDictionary state = new Hashtable();
                    inst.UseNewContext = true;
                    try
                    {
                        if (undo)
                            inst.Uninstall(state);
                        else
                        {
                            inst.Install(state);
                            inst.Commit(state);
                        }
                    }
                    catch
                    {
                        try
                        {
                            inst.Rollback(state);
                        }
                        catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            };
        }
    }

    [RunInstaller(true)]
    public sealed class MyServiceInstallerProcess : ServiceProcessInstaller
    {
        public MyServiceInstallerProcess()
        {
            this.Account = ServiceAccount.NetworkService;
            this.Username = null;
            this.Password = null;
        }
    }

    [RunInstaller(true)]
    public sealed class MyServiceInstaller : ServiceInstaller
    {
        public MyServiceInstaller()
        {
            this.Description = "OruxPals Windows Server (" + OruxPalsServer.softver + ")";
            this.DisplayName = "OruxPals Server";
            this.ServiceName = OruxPalsServer.serviceName;
            this.StartType = System.ServiceProcess.ServiceStartMode.Automatic;
        }
    }   
}
