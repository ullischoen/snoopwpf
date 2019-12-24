﻿// (c) Copyright Bailey Ling.
// This source is subject to the Microsoft Public License (Ms-PL).
// Please see http://go.microsoft.com/fwlink/?LinkID=131993 for details.
// All other rights reserved.

namespace Snoop.PowerShell
{
    using System;
    using System.IO;
    using System.Management.Automation;
    using System.Management.Automation.Runspaces;
    using System.Reflection;
    using System.Threading;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using Snoop.Infrastructure;

    /// <summary>
    /// Interaction logic for EmbeddedShellView.xaml
    /// </summary>
    public partial class EmbeddedShellView : UserControl
    {
        public event Action<VisualTreeItem> ProviderLocationChanged = delegate { }; 

        private Runspace runspace;
        private SnoopPSHost host;
        private int historyIndex;

        private bool isStarted;

        public EmbeddedShellView()
        {
            this.InitializeComponent();

            this.commandTextBox.PreviewKeyDown += this.OnCommandTextBoxPreviewKeyDown;
        }

        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(EmbeddedShellView), new PropertyMetadata(default(bool), OnIsSelectedChanged));
        
        public bool IsSelected
        {
            get { return (bool)this.GetValue(IsSelectedProperty); }
            set { this.SetValue(IsSelectedProperty, value); }
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (EmbeddedShellView)d;

            if ((bool)e.NewValue == false)
            {
                return;
            }

            System.Diagnostics.Debugger.Launch();

            view.WhenLoaded(x => ((EmbeddedShellView)x).Start(VisualTreeHelper2.GetAncestor<SnoopUI>(view)));
        }

        /// <summary>
        /// Initiates the startup routine and configures the runspace for use.
        /// </summary>
        private void Start(SnoopUI snoopUi)
        {
            if (this.isStarted)
            {
                return;
            }

            if (ShellConstants.IsPowerShellInstalled == false)
            {
                return;
            }

            this.isStarted = true;

            {
                // ignore execution-policy
                var iis = InitialSessionState.CreateDefault();
                iis.AuthorizationManager = new AuthorizationManager(Guid.NewGuid().ToString());
                iis.Providers.Add(new SessionStateProviderEntry(ShellConstants.DriveName, typeof(VisualTreeProvider), string.Empty));

                this.host = new SnoopPSHost(x => this.outputTextBox.AppendText(x));
                this.runspace = RunspaceFactory.CreateRunspace(this.host, iis);
                this.runspace.ThreadOptions = PSThreadOptions.UseCurrentThread;

                #if NET40
                this.runspace.ApartmentState = ApartmentState.STA;
                #endif

                this.runspace.Open();

                // default required if you intend to inject scriptblocks into the host application
                Runspace.DefaultRunspace = this.runspace;
            }

            {
                this.Invoke(string.Format("new-psdrive {0} {0} -root /", ShellConstants.DriveName));

                // synchronize selected and root tree elements
                snoopUi.PropertyChanged += (sender, e) =>
                                      {
                                          switch (e.PropertyName)
                                          {
                                              case nameof(SnoopUI.CurrentSelection):
                                                  this.SetVariable(ShellConstants.Selected, snoopUi.CurrentSelection);
                                                  break;
                                              case nameof(SnoopUI.Root):
                                                  this.SetVariable(ShellConstants.Root, snoopUi.Root);
                                                  break;
                                          }
                                      };

                // allow scripting of the host controls
                this.SetVariable("snoopui", snoopUi);
                this.SetVariable("ui", this);
                this.SetVariable(ShellConstants.Root, snoopUi.Root);
                this.SetVariable(ShellConstants.Selected, snoopUi.CurrentSelection);

                // marshall back to the UI thread when the provider notifiers of a location change
                var action = new Action<VisualTreeItem>(item => this.Dispatcher.BeginInvoke(new Action(() => this.ProviderLocationChanged(item))));
                this.SetVariable(ShellConstants.LocationChangedActionKey, action);

                string folder = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "..", "Scripts");
                this.Invoke(string.Format("import-module \"{0}\"", Path.Combine(folder, ShellConstants.SnoopModule)));

                this.outputTextBox.Clear();
                this.Invoke("write-host 'Welcome to the Snoop PowerShell console!'");
                this.Invoke("write-host '----------------------------------------'");
                this.Invoke(string.Format("write-host 'To get started, try using the ${0} and ${1} variables.'", ShellConstants.Root, ShellConstants.Selected));

                this.FindAndLoadProfile(folder);

                this.NotifySelected(snoopUi.CurrentSelection);
            }

            {
                RoutedPropertyChangedEventHandler<object> onSelectedItemChanged = (sender, e) => this.NotifySelected(snoopUi.CurrentSelection);
                Action<VisualTreeItem> onProviderLocationChanged = item => this.Dispatcher.BeginInvoke(new Action(() =>
                                                                                                                  {
                                                                                                                      item.IsSelected = true;
                                                                                                                      snoopUi.CurrentSelection = item;
                                                                                                                  }));

                // sync the current location
                snoopUi.Tree.SelectedItemChanged += onSelectedItemChanged;
                this.ProviderLocationChanged += onProviderLocationChanged;

                // clean up garbage!
                snoopUi.Closed += delegate
                                  {
                                      snoopUi.Tree.SelectedItemChanged -= onSelectedItemChanged;
                                      this.ProviderLocationChanged -= onProviderLocationChanged;
                                  };
            }
        }

        public void SetVariable(string name, object instance)
        {
            // add to the host so the provider has access to exposed variables
            this.host[name] = instance;

            // expose to the current runspace
            this.Invoke(string.Format("${0} = $host.PrivateData['{0}']", name));
        }

        public void NotifySelected(VisualTreeItem item)
        {
            if (this.autoExpandCheckBox.IsChecked == true)
            {
                item.IsExpanded = true;
            }

            this.Invoke(string.Format("cd {0}:\\{1}", ShellConstants.DriveName, item.NodePath()));
        }

        private void FindAndLoadProfile(string scriptFolder)
        {
            if (this.LoadProfile(Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE"), ShellConstants.SnoopProfile)))
            {
                return;
            }

            if (this.LoadProfile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal) + "\\WindowsPowerShell", ShellConstants.SnoopProfile)))
            {
                return;
            }

            this.LoadProfile(Path.Combine(scriptFolder, ShellConstants.SnoopProfile));
        }

        private bool LoadProfile(string scriptPath)
        {
            if (File.Exists(scriptPath))
            {
                this.Invoke("write-host ''");
                this.Invoke(string.Format("${0} = '{1}'; . ${0}", ShellConstants.Profile, scriptPath));
                this.Invoke(string.Format("write-host \"Loaded `$profile: ${0}\"", ShellConstants.Profile));

                return true;
            }

            return false;
        }

        private void OnCommandTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                    this.SetCommandTextToHistory(++this.historyIndex);
                    break;
                case Key.Down:
                    if (this.historyIndex - 1 <= 0)
                    {
                        this.commandTextBox.Clear();
                    }
                    else
                    {
                        this.SetCommandTextToHistory(--this.historyIndex);
                    }
                    break;
                case Key.Return:
                    this.outputTextBox.AppendText(Environment.NewLine);
                    this.outputTextBox.AppendText(this.commandTextBox.Text);
                    this.outputTextBox.AppendText(Environment.NewLine);

                    this.Invoke(this.commandTextBox.Text, true);
                    this.commandTextBox.Clear();
                    break;
            }
        }

        private void Invoke(string script, bool addToHistory = false)
        {
            this.historyIndex = 0;

            try
            {
                using (var pipe = this.runspace.CreatePipeline(script, addToHistory))
                {
                    var cmd = new Command("Out-String");
                    cmd.Parameters.Add("Width", Math.Max(2, (int)(this.ActualWidth * 0.7)));
                    pipe.Commands.Add(cmd);

                    foreach (var item in pipe.Invoke())
                    {
                        this.outputTextBox.AppendText(item.ToString());
                    }

                    foreach (PSObject item in pipe.Error.ReadToEnd())
                    {
                        var error = (ErrorRecord)item.BaseObject;
                        this.OutputErrorRecord(error);
                    }
                }
            }
            catch (RuntimeException ex)
            {
                this.OutputErrorRecord(ex.ErrorRecord);
            }
            catch (Exception ex)
            {
                this.outputTextBox.AppendText(string.Format("Oops!  Uncaught exception invoking on the PowerShell runspace: {0}", ex.Message));
            }

            this.outputTextBox.ScrollToEnd();
        }

        private void OutputErrorRecord(ErrorRecord error)
        {
            this.outputTextBox.AppendText(string.Format("{0}{1}", error, error.InvocationInfo != null ? error.InvocationInfo.PositionMessage : string.Empty));
            this.outputTextBox.AppendText(string.Format("{1}  + CategoryInfo          : {0}", error.CategoryInfo, Environment.NewLine));
            this.outputTextBox.AppendText(string.Format("{1}  + FullyQualifiedErrorId : {0}", error.FullyQualifiedErrorId, Environment.NewLine));
        }

        private void SetCommandTextToHistory(int history)
        {
            var cmd = this.GetHistoryCommand(history);
            if (cmd != null)
            {
                this.commandTextBox.Text = cmd;
                this.commandTextBox.SelectionStart = cmd.Length;
            }
        }

        private string GetHistoryCommand(int history)
        {
            using (var pipe = this.runspace.CreatePipeline("get-history -count " + history, false))
            {
                var results = pipe.Invoke();
                if (results.Count > 0)
                {
                    var item = results[0];
                    return (string)item.Properties["CommandLine"].Value;
                }
                return null;
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            switch (e.Key)
            {
                case Key.F5:
                    this.Invoke(string.Format("if (${0}) {{ . ${0} }}", ShellConstants.Profile));
                    break;
                case Key.F12:
                    this.outputTextBox.Clear();
                    break;
            }
        }
    }
}