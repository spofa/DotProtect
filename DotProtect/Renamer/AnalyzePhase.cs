﻿using System;
using System.Collections.Generic;
using DotProtect.Core;
using DotProtect.Renamer.Analyzers;
using dnlib.DotNet;

namespace DotProtect.Renamer {
	internal class AnalyzePhase : ProtectionPhase {
		public AnalyzePhase(NameProtection parent)
			: base(parent) { }

		public override bool ProcessAll {
			get { return true; }
		}

		public override ProtectionTargets Targets {
			get { return ProtectionTargets.AllDefinitions; }
		}

		public override string Name {
			get { return "Name analysis"; }
		}

		void ParseParameters(IDnlibDef def, DotProtectContext context, NameService service, ProtectionParameters parameters) {
			var mode = parameters.GetParameter<RenameMode?>(context, def, "mode", null);
			if (mode != null)
				service.SetRenameMode(def, mode.Value);
		}

		protected internal override void Execute(DotProtectContext context, ProtectionParameters parameters) {
			var service = (NameService)context.Registry.GetService<INameService>();
			context.Logger.Debug("Building VTables & identifier list...");
			foreach (IDnlibDef def in parameters.Targets.WithProgress(context.Logger)) {
				ParseParameters(def, context, service, parameters);

				if (def is ModuleDef) {
					var module = (ModuleDef)def;
					foreach (Resource res in module.Resources)
						service.SetOriginalName(res, res.Name);
				}
				else
					service.SetOriginalName(def, def.Name);

				if (def is TypeDef) {
					service.GetVTables().GetVTable((TypeDef)def);
					service.SetOriginalNamespace(def, ((TypeDef)def).Namespace);
				}
				context.CheckCancellation();
			}

			context.Logger.Debug("Analyzing...");
			RegisterRenamers(context, service);
			IList<IRenamer> renamers = service.Renamers;
			foreach (IDnlibDef def in parameters.Targets.WithProgress(context.Logger)) {
				Analyze(service, context, parameters, def, true);
				context.CheckCancellation();
			}
		}

		void RegisterRenamers(DotProtectContext context, NameService service) {
			bool wpf = false,
			     caliburn = false,
			     winforms = false,
			     json = false;

			foreach (var module in context.Modules)
				foreach (var asmRef in module.GetAssemblyRefs()) {
					if (asmRef.Name == "WindowsBase" || asmRef.Name == "PresentationCore" ||
					    asmRef.Name == "PresentationFramework" || asmRef.Name == "System.Xaml") {
						wpf = true;
					}
					else if (asmRef.Name == "Caliburn.Micro") {
						caliburn = true;
					}
					else if (asmRef.Name == "System.Windows.Forms") {
						winforms = true;
					}
					else if (asmRef.Name == "Newtonsoft.Json") {
						json = true;
					}
				}

			if (wpf) {
				var wpfAnalyzer = new WPFAnalyzer();
				context.Logger.Debug("WPF found, enabling compatibility.");
				service.Renamers.Add(wpfAnalyzer);
				if (caliburn) {
					context.Logger.Debug("Caliburn.Micro found, enabling compatibility.");
					service.Renamers.Add(new CaliburnAnalyzer(wpfAnalyzer));
				}
			}

			if (winforms) {
				var winformsAnalyzer = new WinFormsAnalyzer();
				context.Logger.Debug("WinForms found, enabling compatibility.");
				service.Renamers.Add(winformsAnalyzer);
			}

			if (json) {
				var jsonAnalyzer = new JsonAnalyzer();
				context.Logger.Debug("Newtonsoft.Json found, enabling compatibility.");
				service.Renamers.Add(jsonAnalyzer);
			}
		}

		internal void Analyze(NameService service, DotProtectContext context, ProtectionParameters parameters, IDnlibDef def, bool runAnalyzer) {
			if (def is TypeDef)
				Analyze(service, context, parameters, (TypeDef)def);
			else if (def is MethodDef)
				Analyze(service, context, parameters, (MethodDef)def);
			else if (def is FieldDef)
				Analyze(service, context, parameters, (FieldDef)def);
			else if (def is PropertyDef)
				Analyze(service, context, parameters, (PropertyDef)def);
			else if (def is EventDef)
				Analyze(service, context, parameters, (EventDef)def);
			else if (def is ModuleDef) {
				var pass = parameters.GetParameter<string>(context, def, "password", null);
				if (pass != null)
					service.reversibleRenamer = new ReversibleRenamer(pass);

				var idOffset = parameters.GetParameter<uint>(context, def, "idOffset", 0);
				if (idOffset != 0)
					service.SetNameId(idOffset);

				service.SetCanRename(def, false);
			}

			if (!runAnalyzer || parameters.GetParameter(context, def, "forceRen", false))
				return;

			foreach (IRenamer renamer in service.Renamers)
				renamer.Analyze(context, service, parameters, def);
		}

		static bool IsVisibleOutside(DotProtectContext context, ProtectionParameters parameters, IMemberDef def) {
			var type = def as TypeDef;
			if (type == null)
				type = def.DeclaringType;

			var renPublic = parameters.GetParameter<bool?>(context, def, "renPublic", null);
			if (renPublic == null)
				return type.IsVisibleOutside();
			else
				return type.IsVisibleOutside(false) && !renPublic.Value;
		}

		void Analyze(NameService service, DotProtectContext context, ProtectionParameters parameters, TypeDef type) {
			if (IsVisibleOutside(context, parameters, type)) {
				service.SetCanRename(type, false);
			}
			else if (type.IsRuntimeSpecialName || type.IsGlobalModuleType) {
				service.SetCanRename(type, false);
			}
			else if (type.FullName == "ConfusedByAttribute") {
				// Courtesy
				service.SetCanRename(type, false);
			}

			/*
			 * Can't rename Classes/Types that will be serialized
			 */
			if(type != null) {
				if (type.IsSerializable) {
					service.SetCanRename(type, false);
				}

				if (type.DeclaringType != null) {
					if (type.DeclaringType.IsSerializable) {
						service.SetCanRename(type, false);
					}
				}
			}

			if (parameters.GetParameter(context, type, "forceRen", false))
				return;

			if (type.InheritsFromCorlib("System.Attribute")) {
				service.ReduceRenameMode(type, RenameMode.ASCII);
			}

			if (type.InheritsFrom("System.Configuration.SettingsBase")) {
				service.SetCanRename(type, false);
			}
		}

		void Analyze(NameService service, DotProtectContext context, ProtectionParameters parameters, MethodDef method) {
			if (IsVisibleOutside(context, parameters, method.DeclaringType) &&
			    (method.IsFamily || method.IsFamilyOrAssembly || method.IsPublic) &&
			    IsVisibleOutside(context, parameters, method))
				service.SetCanRename(method, false);

			else if (method.IsRuntimeSpecialName)
				service.SetCanRename(method, false);

			else if (parameters.GetParameter(context, method, "forceRen", false))
				return;

			else if (method.DeclaringType.IsComImport() && !method.HasAttribute("System.Runtime.InteropServices.DispIdAttribute"))
				service.SetCanRename(method, false);

			else if (method.DeclaringType.IsDelegate())
				service.SetCanRename(method, false);
		}

		void Analyze(NameService service, DotProtectContext context, ProtectionParameters parameters, FieldDef field) {
			if (IsVisibleOutside(context, parameters, field.DeclaringType) &&
			    (field.IsFamily || field.IsFamilyOrAssembly || field.IsPublic) &&
			    IsVisibleOutside(context, parameters, field))
				service.SetCanRename(field, false);

			else if (field.IsRuntimeSpecialName)
				service.SetCanRename(field, false);

			else if (parameters.GetParameter(context, field, "forceRen", false))
				return;

			/*
			 * System.Xml.Serialization.XmlSerializer
			 * 
			 * XmlSerializer by default serializes fields marked with [NonSerialized]
			 * This is a work-around that causes all fields in a class marked [Serializable]
			 * to _not_ be renamed, unless marked with [XmlIgnoreAttribute]
			 * 
			 * If we have a way to detect which serializer method the code is going to use
			 * for the class, or if Microsoft makes XmlSerializer respond to [NonSerialized]
			 * we'll have a more accurate way to achieve this.
			 */
			else if (field.DeclaringType.IsSerializable) // && !field.IsNotSerialized)
				service.SetCanRename(field, false);

			else if (field.DeclaringType.IsSerializable && (field.CustomAttributes.IsDefined("XmlIgnore")
														|| field.CustomAttributes.IsDefined("XmlIgnoreAttribute")
														|| field.CustomAttributes.IsDefined("System.Xml.Serialization.XmlIgnore")
														|| field.CustomAttributes.IsDefined("System.Xml.Serialization.XmlIgnoreAttribute")
														|| field.CustomAttributes.IsDefined("T:System.Xml.Serialization.XmlIgnoreAttribute"))) // Can't seem to detect CustomAttribute
				service.SetCanRename(field, true);
			/*
			 * End of XmlSerializer work-around
			 */

			else if (field.IsLiteral && field.DeclaringType.IsEnum &&
				!parameters.GetParameter(context, field, "renEnum", false))
				service.SetCanRename(field, false);
		}

		void Analyze(NameService service, DotProtectContext context, ProtectionParameters parameters, PropertyDef property) {
			if (IsVisibleOutside(context, parameters, property.DeclaringType) &&
			    IsVisibleOutside(context, parameters, property))
				service.SetCanRename(property, false);

			else if (property.IsRuntimeSpecialName)
				service.SetCanRename(property, false);

			else if (parameters.GetParameter(context, property, "forceRen", false))
				return;

            /*
             * System.Xml.Serialization.XmlSerializer
             * 
             * XmlSerializer by default serializes fields marked with [NonSerialized]
             * This is a work-around that causes all fields in a class marked [Serializable]
             * to _not_ be renamed, unless marked with [XmlIgnoreAttribute]
             * 
             * If we have a way to detect which serializer method the code is going to use
             * for the class, or if Microsoft makes XmlSerializer respond to [NonSerialized]
             * we'll have a more accurate way to achieve this.
             */
            else if (property.DeclaringType.IsSerializable) // && !field.IsNotSerialized)
                service.SetCanRename(property, false);

            else if (property.DeclaringType.IsSerializable && (property.CustomAttributes.IsDefined("XmlIgnore")
                                                        || property.CustomAttributes.IsDefined("XmlIgnoreAttribute")
                                                        || property.CustomAttributes.IsDefined("System.Xml.Serialization.XmlIgnore")
                                                        || property.CustomAttributes.IsDefined("System.Xml.Serialization.XmlIgnoreAttribute")
                                                        || property.CustomAttributes.IsDefined("T:System.Xml.Serialization.XmlIgnoreAttribute"))) // Can't seem to detect CustomAttribute
                service.SetCanRename(property, true);
            /*
			 * End of XmlSerializer work-around
			 */

            else if (property.DeclaringType.Implements("System.ComponentModel.INotifyPropertyChanged"))
				service.SetCanRename(property, false);

			else if (property.DeclaringType.Name.String.Contains("AnonymousType"))
				service.SetCanRename(property, false);
		}

		void Analyze(NameService service, DotProtectContext context, ProtectionParameters parameters, EventDef evt) {
			if (IsVisibleOutside(context, parameters, evt.DeclaringType) &&
			    IsVisibleOutside(context, parameters, evt))
				service.SetCanRename(evt, false);

			else if (evt.IsRuntimeSpecialName)
				service.SetCanRename(evt, false);
		}
	}
}