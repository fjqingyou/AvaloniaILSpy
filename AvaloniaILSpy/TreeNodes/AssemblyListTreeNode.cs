﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using ICSharpCode.Decompiler;
using AvaloniaILSpy.Controls;
using Mono.Cecil;

namespace AvaloniaILSpy.TreeNodes
{
	/// <summary>
	/// Represents a list of assemblies.
	/// This is used as (invisible) root node of the tree view.
	/// </summary>
	sealed class AssemblyListTreeNode : ILSpyTreeNode
	{
		readonly AssemblyList assemblyList;

		public AssemblyList AssemblyList
		{
			get { return assemblyList; }
		}

		public AssemblyListTreeNode(AssemblyList assemblyList)
		{
			if (assemblyList == null)
				throw new ArgumentNullException(nameof(assemblyList));
			this.assemblyList = assemblyList;
			BindToObservableCollection(assemblyList.assemblies);
		}

		public override object Text
		{
			get { return assemblyList.ListName; }
		}

		void BindToObservableCollection(ObservableCollection<LoadedAssembly> collection)
		{
			this.Children.Clear();
			this.Children.AddRange(collection.Select(a => new AssemblyTreeNode(a)));
			collection.CollectionChanged += delegate(object sender, NotifyCollectionChangedEventArgs e) {
				switch (e.Action) {
					case NotifyCollectionChangedAction.Add:
						this.Children.InsertRange(e.NewStartingIndex, e.NewItems.Cast<LoadedAssembly>().Select(a => new AssemblyTreeNode(a)));
						break;
					case NotifyCollectionChangedAction.Remove:
						this.Children.RemoveRange(e.OldStartingIndex, e.OldItems.Count);
						break;
					case NotifyCollectionChangedAction.Replace:
					case NotifyCollectionChangedAction.Move:
						throw new NotImplementedException();
					case NotifyCollectionChangedAction.Reset:
						this.Children.Clear();
						this.Children.AddRange(collection.Select(a => new AssemblyTreeNode(a)));
						break;
					default:
						throw new NotSupportedException("Invalid value for NotifyCollectionChangedAction");
				}
			};
		}

		public override bool CanDrop(DragEventArgs e, int index)
		{
			e.DragEffects = DragDropEffects.Move;
			if (e.Data.Contains(AssemblyTreeNode.DataFormat))
				return true;
			else if (e.Data.Contains(DataFormats.FileNames))
				return true;
			else {
				e.DragEffects = DragDropEffects.None;
				return false;
			}
		}

		public override void Drop(DragEventArgs e, int index)
		{	
			string[] files = e.Data.Get(AssemblyTreeNode.DataFormat) as string[];
			if (files == null)
				files = e.Data.Get(DataFormats.FileNames) as string[];
			if (files != null) {
				lock (assemblyList.assemblies) {
					var assemblies = files
						.Where(file => file != null)
						.SelectMany(file => OpenAssembly(assemblyList, file))
						.Where(asm => asm != null)
						.Distinct()
						.ToArray();
					foreach (LoadedAssembly asm in assemblies) {
						int nodeIndex = assemblyList.assemblies.IndexOf(asm);
						if (nodeIndex < index)
							index--;
						assemblyList.assemblies.RemoveAt(nodeIndex);
					}
					assemblies.Reverse();
					foreach (LoadedAssembly asm in assemblies) {
						assemblyList.assemblies.Insert(index, asm);
					}
				}
			}
		}

		private IEnumerable<LoadedAssembly> OpenAssembly(AssemblyList assemblyList, string file)
		{
			if (file.EndsWith(".nupkg")) {
				LoadedNugetPackage package = new LoadedNugetPackage(file);
				// TODO: show dialog
				//var selectionDialog = new NugetPackageBrowserDialog(package);
				//selectionDialog.Owner = Application.Current.MainWindow;
				//if (await selectionDialog.ShowDialog<bool>() != true)
				//	yield break;
				//foreach (var entry in selectionDialog.SelectedItems) {
				foreach (var entry in package.SelectedEntries) {
					var nugetAsm = assemblyList.OpenAssembly("nupkg://" + file + ";" + entry.Name, entry.Stream, true);
					if (nugetAsm != null) {
						yield return nugetAsm;
					}
				}
				yield break;
			}
			yield return assemblyList.OpenAssembly(file);
		}

		public Action<SharpTreeNode> Select = delegate { };

		public override void Decompile(Language language, ITextOutput output, DecompilationOptions options)
		{
			language.WriteCommentLine(output, "List: " + assemblyList.ListName);
			output.WriteLine();
			foreach (AssemblyTreeNode asm in this.Children) {
				language.WriteCommentLine(output, new string('-', 60));
				output.WriteLine();
				asm.Decompile(language, output, options);
			}
		}

		#region Find*Node

		public ILSpyTreeNode FindResourceNode(Resource resource)
		{
			if (resource == null)
				return null;
			foreach (AssemblyTreeNode node in this.Children)
			{
				if (node.LoadedAssembly.IsLoaded)
				{
					node.EnsureLazyChildren();
					foreach (var item in node.Children.OfType<ResourceListTreeNode>())
					{
						var founded = item.Children.OfType<ResourceTreeNode>().Where(x => x.Resource == resource).FirstOrDefault();
						if (founded != null)
							return founded;

						var foundedResEntry = item.Children.OfType<ResourceEntryNode>().Where(x => resource.Name.Equals(x.Text)).FirstOrDefault();
						if (foundedResEntry != null)
							return foundedResEntry;
					}
				}
			}
			return null;
		}


		public AssemblyTreeNode FindAssemblyNode(ModuleDefinition module)
		{
			if (module == null)
				return null;
			App.Current.MainWindow.VerifyAccess();
			foreach (AssemblyTreeNode node in this.Children) {
				if (node.LoadedAssembly.IsLoaded && node.LoadedAssembly.GetModuleDefinitionOrNull() == module)
					return node;
			}
			return null;
		}

		public AssemblyTreeNode FindAssemblyNode(AssemblyDefinition asm)
		{
			if (asm == null)
				return null;
			App.Current.MainWindow.VerifyAccess();
			foreach (AssemblyTreeNode node in this.Children) {
				if (node.LoadedAssembly.IsLoaded && node.LoadedAssembly.GetAssemblyDefinitionOrNull() == asm)
					return node;
			}
			return null;
		}

		public AssemblyTreeNode FindAssemblyNode(LoadedAssembly asm)
		{
			if (asm == null)
				return null;
			App.Current.MainWindow.VerifyAccess();
			foreach (AssemblyTreeNode node in this.Children) {
				if (node.LoadedAssembly == asm)
					return node;
			}
			return null;
		}

		/// <summary>
		/// Looks up the type node corresponding to the type definition.
		/// Returns null if no matching node is found.
		/// </summary>
		public TypeTreeNode FindTypeNode(TypeDefinition def)
		{
			if (def == null)
				return null;
			if (def.DeclaringType != null) {
				TypeTreeNode decl = FindTypeNode(def.DeclaringType);
				if (decl != null) {
					decl.EnsureLazyChildren();
					return decl.Children.OfType<TypeTreeNode>().FirstOrDefault(t => t.TypeDefinition == def && !t.IsHidden);
				}
			} else {
				AssemblyTreeNode asm = FindAssemblyNode(def.Module.Assembly);
				if (asm != null) {
					return asm.FindTypeNode(def);
				}
			}
			return null;
		}

		/// <summary>
		/// Looks up the method node corresponding to the method definition.
		/// Returns null if no matching node is found.
		/// </summary>
		public ILSpyTreeNode FindMethodNode(MethodDefinition def)
		{
			if (def == null)
				return null;
			TypeTreeNode typeNode = FindTypeNode(def.DeclaringType);
			if (typeNode == null)
				return null;
			typeNode.EnsureLazyChildren();
			MethodTreeNode methodNode = typeNode.Children.OfType<MethodTreeNode>().FirstOrDefault(m => m.MethodDefinition == def && !m.IsHidden);
			if (methodNode != null)
				return methodNode;
			foreach (var p in typeNode.Children.OfType<ILSpyTreeNode>()) {
				if (p.IsHidden)
					continue;

				// method might be a child of a property or event
				if (p is PropertyTreeNode || p is EventTreeNode) {
					p.EnsureLazyChildren();
					methodNode = p.Children.OfType<MethodTreeNode>().FirstOrDefault(m => m.MethodDefinition == def);
					if (methodNode != null) {
						// If the requested method is a property or event accessor, and accessors are
						// hidden in the UI, then return the owning property or event.
						if (methodNode.IsHidden)
							return p;
						else
							return methodNode;
					}
				}
			}

			return null;
		}

		/// <summary>
		/// Looks up the field node corresponding to the field definition.
		/// Returns null if no matching node is found.
		/// </summary>
		public FieldTreeNode FindFieldNode(FieldDefinition def)
		{
			if (def == null)
				return null;
			TypeTreeNode typeNode = FindTypeNode(def.DeclaringType);
			if (typeNode == null)
				return null;
			typeNode.EnsureLazyChildren();
			return typeNode.Children.OfType<FieldTreeNode>().FirstOrDefault(m => m.FieldDefinition == def && !m.IsHidden);
		}

		/// <summary>
		/// Looks up the property node corresponding to the property definition.
		/// Returns null if no matching node is found.
		/// </summary>
		public PropertyTreeNode FindPropertyNode(PropertyDefinition def)
		{
			if (def == null)
				return null;
			TypeTreeNode typeNode = FindTypeNode(def.DeclaringType);
			if (typeNode == null)
				return null;
			typeNode.EnsureLazyChildren();
			return typeNode.Children.OfType<PropertyTreeNode>().FirstOrDefault(m => m.PropertyDefinition == def && !m.IsHidden);
		}

		/// <summary>
		/// Looks up the event node corresponding to the event definition.
		/// Returns null if no matching node is found.
		/// </summary>
		public EventTreeNode FindEventNode(EventDefinition def)
		{
			if (def == null)
				return null;
			TypeTreeNode typeNode = FindTypeNode(def.DeclaringType);
			if (typeNode == null)
				return null;
			typeNode.EnsureLazyChildren();
			return typeNode.Children.OfType<EventTreeNode>().FirstOrDefault(m => m.EventDefinition == def && !m.IsHidden);
		}
		#endregion
	}
}
