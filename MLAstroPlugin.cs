using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using MLAstro_Robotic_Polar_Alignment.Dockables;
using MLAstro_Robotic_Polar_Alignment.Plugin;
using MLAstro_Robotic_Polar_Alignment.Services;

namespace NINA.Plugins.PolarAlignment {
    /// <summary>
    /// Single IPluginManifest of the MLAstro plugin (hardware control + dockables + options page).
    /// The TPPA (Three Point Polar Alignment) part has been removed: TPPA is now a separate plugin and
    /// talks to this one only through the NINA message broker.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class MLAstroPlugin : PluginBase, INotifyPropertyChanged {
        /// <summary>Assembly Guid; unchanged so existing MLAstro settings stay valid.</summary>
        public static string PluginId { get; private set; }

        /// <summary>
        /// MLAstro options/state controller backing the CONTROL / CONNECTION / CONFIGURATION tabs of
        /// the options page. DataContext for those tabs is this instance's MLAstro property.
        /// </summary>
        public MLAstroController MLAstro { get; private set; }

        [ImportingConstructor]
        public MLAstroPlugin(global::MLAstro_Robotic_Polar_Alignment.Settings.PluginSettings settings,
            SerialConnectionService serialConnectionService,
            PolarAlignmentDockVM polarAlignmentDockVM,
            IMessageBroker messageBroker) {
            MLAstro = new MLAstroController(settings, serialConnectionService, polarAlignmentDockVM, messageBroker);
            PluginId = this.Identifier;
            Logger.Info("[MLAstro] MLAstroPlugin created");
        }

        public override Task Teardown() {
            Logger.Info("[MLAstro] MLAstroPlugin.Teardown");
            try { MLAstro?.Dispose(); } catch (Exception ex) { Logger.Error(ex); }
            return Task.CompletedTask;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
