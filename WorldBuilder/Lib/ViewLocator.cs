using Avalonia.Controls;
using Avalonia.Controls.Templates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Lib {
    public class ViewLocator : IDataTemplate {
        public Control Build(object? data) {
            if (data is null) {
                return new TextBlock { Text = "data was null" };
            }

            var fullVmName = data.GetType().FullName
                ?? throw new InvalidOperationException($"{data.GetType().FullName} is not a ViewModel");
            var name = fullVmName.Replace("ViewModel", "View");
#pragma warning disable IL2057 // Unrecognized value passed to the parameter of method. It's not possible to guarantee the availability of the target type.
            var type = Type.GetType(name);

            // Fallback: search in current assembly if not found
            if (type == null) {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                type = asm.GetType(name);
            }

            // Fallback: try inserting .Views. before the class name
            // e.g. Namespace.FooViewModel -> Namespace.Views.FooView
            if (type == null) {
                var vmTypeName = data.GetType().Name.Replace("ViewModel", "View");
                var vmNamespace = data.GetType().Namespace;
                if (vmNamespace != null) {
                    var viewsName = vmNamespace + ".Views." + vmTypeName;
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    type = asm.GetType(viewsName);
                    if (type == null) {
                        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) {
                            type = a.GetType(viewsName);
                            if (type != null) break;
                        }
                    }
                    if (type != null) name = viewsName;
                }
            }

            // Fallback: search in all loaded assemblies
            if (type == null) {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    type = asm.GetType(name);
                    if (type != null) break;
                }
            }
#pragma warning restore IL2057 // Unrecognized value passed to the parameter of method. It's not possible to guarantee the availability of the target type.

            Console.WriteLine($"Request: {data.GetType().FullName} -> {name}");

            if (type == null) {
                return new TextBlock { Text = "Not Found: " + name };
            }

            var control = ProjectManager.Instance?.GetProjectService<Control>(type);
            if (control != null) {
                return (Control)control!;
            }

            control = App.Services?.GetService(type) as Control;

            if (control != null) {
                return (Control)control!;
            }

            try {
                control = Activator.CreateInstance(type) as Control;
            }
            catch (MissingMethodException) {
                return new TextBlock { Text = $"No view: {type.Name}" };
            }

            if (control != null) {
                return (Control)control!;
            }

            return new TextBlock { Text = $"Not Found: {type.Name} {name}" };
        }

        public bool Match(object? data) {
            return data is ViewModelBase;
        }
    }
}
