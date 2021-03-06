//
// location.cs: Keeps track of the location of source code entity
//
// Author:
//   Miguel de Icaza
//   Atsushi Enomoto  <atsushi@ximian.com>
//   Marek Safar (marek.safar@gmail.com)
//
// Copyright 2001 Ximian, Inc.
// Copyright 2005 Novell, Inc.
//

using System;
using System.IO;
using System.Collections.Generic;
using Mono.CompilerServices.SymbolWriter;
using System.Diagnostics;
using System.Linq;

namespace Mono.CSharp {
	/// <summary>
	///   This is one single source file.
	/// </summary>
	/// <remarks>
	///   This is intentionally a class and not a struct since we need
	///   to pass this by reference.
	/// </remarks>
	public class SourceFile : ISourceFile, IEquatable<SourceFile>
	{
		public readonly string Name;
		public readonly string FullPathName;
		public readonly int Index;
		public bool AutoGenerated;

		SourceFileEntry file;
		byte[] guid, checksum;

		public SourceFile (string name, string path, int index)
		{
			this.Index = index;
			this.Name = name;
			this.FullPathName = path;
		}

		public SourceFileEntry SourceFileEntry {
			get { return file; }
		}

		SourceFileEntry ISourceFile.Entry {
			get { return file; }
		}

		public void SetChecksum (byte[] guid, byte[] checksum)
		{
			this.guid = guid;
			this.checksum = checksum;
		}

		public virtual void DefineSymbolInfo (MonoSymbolWriter symwriter)
		{
			if (guid != null)
				file = symwriter.DefineDocument (FullPathName, guid, checksum);
			else {
				file = symwriter.DefineDocument (FullPathName);
				if (AutoGenerated)
					file.SetAutoGenerated ();
			}
		}

		public bool Equals (SourceFile other)
		{
			return FullPathName == other.FullPathName;
		}

		public override string ToString ()
		{
			return String.Format ("SourceFile ({0}:{1}:{2}:{3})",
						  Name, FullPathName, Index, SourceFileEntry);
		}
	}

	public class CompilationSourceFile : SourceFile, ICompileUnit
	{
		CompileUnitEntry comp_unit;
		Dictionary<string, SourceFile> include_files;
		Dictionary<string, bool> conditionals;
		NamespaceContainer ns_container;

		public CompilationSourceFile (string name, string fullPathName, int index)
			: base (name, fullPathName, index)
		{
		}

		CompileUnitEntry ICompileUnit.Entry {
			get { return comp_unit; }
		}

		public CompileUnitEntry CompileUnitEntry {
			get { return comp_unit; }
		}

		public NamespaceContainer NamespaceContainer {
			get {
				return ns_container;
			}
			set {
				ns_container = value;
			}
		}

		public void AddIncludeFile (SourceFile file)
		{
			if (file == this)
				return;
			
			if (include_files == null)
				include_files = new Dictionary<string, SourceFile> ();

			if (!include_files.ContainsKey (file.FullPathName))
				include_files.Add (file.FullPathName, file);
		}

		public void AddDefine (string value)
		{
			if (conditionals == null)
				conditionals = new Dictionary<string, bool> (2);

			conditionals [value] = true;
		}

		public void AddUndefine (string value)
		{
			if (conditionals == null)
				conditionals = new Dictionary<string, bool> (2);

			conditionals [value] = false;
		}

		public override void DefineSymbolInfo (MonoSymbolWriter symwriter)
		{
			base.DefineSymbolInfo (symwriter);

			comp_unit = symwriter.DefineCompilationUnit (SourceFileEntry);

			if (include_files != null) {
				foreach (SourceFile include in include_files.Values) {
					include.DefineSymbolInfo (symwriter);
					comp_unit.AddFile (include.SourceFileEntry);
				}
			}
		}

		public bool IsConditionalDefined (CompilerContext ctx, string value)
		{
			if (conditionals != null) {
				bool res;
				if (conditionals.TryGetValue (value, out res))
					return res;
				
				// When conditional was undefined
				if (conditionals.ContainsKey (value))
					return false;					
			}

			return ctx.Settings.IsConditionalSymbolDefined (value);
		}
	}

	/// <summary>
	///   Keeps track of the location in the program
	/// </summary>
	///
	/// <remarks>
	///   This uses a compact representation and a couple of auxiliary
	///   structures to keep track of tokens to (file,line and column) 
	///   mappings. The usage of the bits is:
	///   
	///     - 16 bits for "checkpoint" which is a mixed concept of
	///       file and "line segment"
	///     - 8 bits for line delta (offset) from the line segment
	///     - 8 bits for column number.
	///
	///   http://lists.ximian.com/pipermail/mono-devel-list/2004-December/009508.html
	/// </remarks>
	public struct Location : IEquatable<Location>
	{
		struct Checkpoint {
			public readonly int LineOffset;
			public readonly int CompilationUnit;
			public readonly int File;

			public Checkpoint (int compile_unit, int file, int line)
			{
				File = file;
				CompilationUnit = compile_unit;
				LineOffset = line - (int) (line % (1 << line_delta_bits));
			}
		}

#if FULL_AST
		long token;

		const int column_bits = 24;
		const int line_delta_bits = 24;
#else
		int token;

		const int column_bits = 8;
		const int line_delta_bits = 8;
#endif
		const int checkpoint_bits = 16;

		// -2 because the last one is used for hidden
		const int max_column = (1 << column_bits) - 2;
		const int column_mask = (1 << column_bits) - 1;

		static List<SourceFile> source_list;
		static int current_source;
		static int current_compile_unit;
		static Checkpoint [] checkpoints;
		static int checkpoint_index;
		
		public readonly static Location Null = new Location (-1);
		public static bool InEmacs;
		
		static Location ()
		{
			Reset ();
		}

		public static void Reset ()
		{
			source_list = new List<SourceFile> ();
			current_source = 0;
			current_compile_unit = 0;
			checkpoint_index = 0;
		}

		public static SourceFile AddFile (string name, string fullName)
		{
			var source = new SourceFile (name, fullName, source_list.Count + 1);
			source_list.Add (source);
			return source;
		}

		// <summary>
		//   After adding all source files we want to compile with AddFile(), this method
		//   must be called to `reserve' an appropriate number of bits in the token for the
		//   source file.  We reserve some extra space for files we encounter via #line
		//   directives while parsing.
		// </summary>
		static public void Initialize (List<CompilationSourceFile> files)
		{
#if NET_4_0
			source_list.AddRange (files);
#else
			source_list.AddRange (files.ToArray ());
#endif

			checkpoints = new Checkpoint [source_list.Count * 2];
			if (checkpoints.Length > 0)
				checkpoints [0] = new Checkpoint (0, 0, 0);
		}

		static public void Push (CompilationSourceFile compile_unit, SourceFile file)
		{
			current_source = file != null ? file.Index : -1;
			current_compile_unit = compile_unit != null ? compile_unit.Index : -1;
			// File is always pushed before being changed.
		}
		
		public Location (int row)
			: this (row, 0)
		{
		}

		public Location (int row, int column)
		{
			if (row <= 0)
				token = 0;
			else {
				if (column > max_column)
					column = max_column;
				else if (column < 0)
					column = max_column + 1;

				long target = -1;
				long delta = 0;

				// FIXME: This value is certainly wrong but what was the intension
				int max = checkpoint_index < 10 ?
					checkpoint_index : 10;
				for (int i = 0; i < max; i++) {
					int offset = checkpoints [checkpoint_index - i].LineOffset;
					delta = row - offset;
					if (delta >= 0 &&
						delta < (1 << line_delta_bits) &&
						checkpoints [checkpoint_index - i].File == current_source) {
						target = checkpoint_index - i;
						break;
					}
				}
				if (target == -1) {
					AddCheckpoint (current_compile_unit, current_source, row);
					target = checkpoint_index;
					delta = row % (1 << line_delta_bits);
				}

				long l = column +
					(delta << column_bits) +
					(target << (line_delta_bits + column_bits));
#if FULL_AST
				token = l;
#else
				token = l > 0xFFFFFFFF ? 0 : (int) l;
#endif
			}
		}

		public static Location operator - (Location loc, int columns)
		{
			return new Location (loc.Row, loc.Column - columns);
		}

		static void AddCheckpoint (int compile_unit, int file, int row)
		{
			if (checkpoints.Length == ++checkpoint_index) {
				Array.Resize (ref checkpoints, checkpoint_index * 2);
			}
			checkpoints [checkpoint_index] = new Checkpoint (compile_unit, file, row);
		}

		string FormatLocation (string fileName)
		{
			if (column_bits == 0 || InEmacs)
				return fileName + "(" + Row.ToString () + "):";

			return fileName + "(" + Row.ToString () + "," + Column.ToString () +
				(Column == max_column ? "+):" : "):");
		}
		
		public override string ToString ()
		{
			return FormatLocation (Name);
		}

		public string ToStringFullName ()
		{
			return FormatLocation (NameFullPath);
		}
		
		/// <summary>
		///   Whether the Location is Null
		/// </summary>
		public bool IsNull {
			get { return token == 0; }
		}

		public string Name {
			get {
				int index = File;
				if (token == 0 || index == 0)
					return "Internal";
				if (source_list == null || index - 1 >= source_list.Count)
					return "unknown_file";

				SourceFile file = source_list [index - 1];
				return file.Name;
			}
		}

		public string NameFullPath {
			get {
				int index = File;
				if (token == 0 || index == 0)
					return "Internal";

				return source_list[index - 1].FullPathName;
			}
		}

		int CheckpointIndex {
			get {
				const int checkpoint_mask = (1 << checkpoint_bits) - 1;
				return ((int) (token >> (line_delta_bits + column_bits))) & checkpoint_mask;
			}
		}

		public int Row {
			get {
				if (token == 0)
					return 1;

				int offset = checkpoints[CheckpointIndex].LineOffset;

				const int line_delta_mask = (1 << column_bits) - 1;
				return offset + (((int)(token >> column_bits)) & line_delta_mask);
			}
		}

		public int Column {
			get {
				if (token == 0)
					return 1;
				int col = (int) (token & column_mask);
				return col > max_column ? 1 : col;
			}
		}

		public bool Hidden {
			get {
				return (int) (token & column_mask) == max_column + 1;
			}
		}

		public int CompilationUnitIndex {
			get {
				if (token == 0)
					return 0;
if (checkpoints.Length <= CheckpointIndex) throw new Exception (String.Format ("Should not happen. Token is {0:X04}, checkpoints are {1}, index is {2}", token, checkpoints.Length, CheckpointIndex));
				return checkpoints [CheckpointIndex].CompilationUnit;
			}
		}

		public int File {
			get {
				if (token == 0)
					return 0;
if (checkpoints.Length <= CheckpointIndex) throw new Exception (String.Format ("Should not happen. Token is {0:X04}, checkpoints are {1}, index is {2}", token, checkpoints.Length, CheckpointIndex));
				return checkpoints [CheckpointIndex].File;
			}
		}

		// The ISymbolDocumentWriter interface is used by the symbol writer to
		// describe a single source file - for each source file there's exactly
		// one corresponding ISymbolDocumentWriter instance.
		//
		// This class has an internal hash table mapping source document names
		// to such ISymbolDocumentWriter instances - so there's exactly one
		// instance per document.
		//
		// This property returns the ISymbolDocumentWriter instance which belongs
		// to the location's source file.
		//
		// If we don't have a symbol writer, this property is always null.
		public SourceFile SourceFile {
			get {
				int index = File;
				if (index == 0)
					return null;
				return (SourceFile) source_list [index - 1];
			}
		}

		public CompilationSourceFile CompilationUnit {
			get {
				int index = CompilationUnitIndex;
				if (index == 0)
					return null;
				return (CompilationSourceFile) source_list [index - 1];
			}
		}

		#region IEquatable<Location> Members

		public bool Equals (Location other)
		{
			return this.token == other.token;
		}

		#endregion
	}
	
	public class SpecialsBag
	{
		public enum CommentType
		{
			Single,
			Multi,
			Documentation,
			InactiveCode
		}
		
		public class Comment
		{
			public readonly CommentType CommentType;
			public readonly bool StartsLine;
			public readonly int Line;
			public readonly int Col;
			public readonly int EndLine;
			public readonly int EndCol;
			public readonly string Content;
			
			public Comment (CommentType commentType, bool startsLine, int line, int col, int endLine, int endCol, string content)
			{
				this.CommentType = commentType;
				this.StartsLine = startsLine;
				this.Line = line;
				this.Col = col;
				this.EndLine = endLine;
				this.EndCol = endCol;
				this.Content = content;
			}

			public override string ToString ()
			{
				return string.Format ("[Comment: CommentType={0}, Line={1}, Col={2}, EndLine={3}, EndCol={4}, Content={5}]", CommentType, Line, Col, EndLine, EndCol, Content);
			}
		}
		
		public class PreProcessorDirective
		{
			public readonly int Line;
			public readonly int Col;
			public readonly int EndLine;
			public readonly int EndCol;

			public readonly Tokenizer.PreprocessorDirective Cmd;
			public readonly string Arg;
			
			public bool Take = true;
			
			public PreProcessorDirective (int line, int col, int endLine, int endCol, Tokenizer.PreprocessorDirective cmd, string arg)
			{
				this.Line = line;
				this.Col = col;
				this.EndLine = endLine;
				this.EndCol = endCol;
				this.Cmd = cmd;
				this.Arg = arg;
			}
			
			public override string ToString ()
			{
				return string.Format ("[PreProcessorDirective: Line={0}, Col={1}, EndLine={2}, EndCol={3}, Cmd={4}, Arg={5}]", Line, Col, EndLine, EndCol, Cmd, Arg);
			}
		}
		
		public readonly List<object> Specials = new List<object> ();
		
		CommentType curComment;
		bool startsLine;
		int startLine, startCol;
		System.Text.StringBuilder contentBuilder = new System.Text.StringBuilder ();
		
		[Conditional ("FULL_AST")]
		public void StartComment (CommentType type, bool startsLine, int startLine, int startCol)
		{
			inComment = true;
			curComment = type;
			this.startsLine = startsLine;
			this.startLine = startLine;
			this.startCol = startCol;
			contentBuilder.Length = 0;
		}
		
		[Conditional ("FULL_AST")]
		public void PushCommentChar (int ch)
		{
			if (ch < 0)
				return;
			contentBuilder.Append ((char)ch);
		}
		[Conditional ("FULL_AST")]
		public void PushCommentString (string str)
		{
			contentBuilder.Append (str);
		}
		
		bool inComment;
		[Conditional ("FULL_AST")]
		public void EndComment (int endLine, int endColumn)
		{
			if (!inComment)
				return;
			inComment = false;
			Specials.Add (new Comment (curComment, startsLine, startLine, startCol, endLine, endColumn, contentBuilder.ToString ()));
		}
		
		[Conditional ("FULL_AST")]
		public void AddPreProcessorDirective (int startLine, int startCol, int endLine, int endColumn, Tokenizer.PreprocessorDirective cmd, string arg)
		{
			if (inComment)
				EndComment (startLine, startCol);
			Specials.Add (new PreProcessorDirective (startLine, startCol, endLine, endColumn, cmd, arg));
		}

		public void SkipIf ()
		{
			if (Specials.Count > 0) {
				var directive = Specials[Specials.Count - 1] as PreProcessorDirective;
				if (directive != null)
					directive.Take = false;
			}
		}
	}

	//
	// A bag of additional locations to support full ast tree
	//
	public class LocationsBag
	{
		public class MemberLocations
		{
			public IList<Tuple<Modifiers, Location>> Modifiers { get; internal set; }
			List<Location> locations;
			
			public MemberLocations (IList<Tuple<Modifiers, Location>> mods, IEnumerable<Location> locs)
			{
				Modifiers = mods;
				locations = locs != null ?  new List<Location> (locs) : null;
			}

			#region Properties

			public Location this [int index] {
				get {
					return locations [index];
				}
			}
			
			public int Count {
				get {
					return locations != null ? locations.Count : 0;
				}
			}

			#endregion

			public void AddLocations (params Location[] additional)

			{

				AddLocations ((IEnumerable<Location>)additional);

			}
			public void AddLocations (IEnumerable<Location> additional)
			{
				if (additional == null)
					return;
				if (locations == null) {
					locations = new List<Location>(additional);
				} else {
					locations.AddRange (additional);
				}
			}
		}
		
		public MemberCore LastMember {
			get;
			private set;
		}

		Dictionary<object, List<Location>> simple_locs = new Dictionary<object,  List<Location>> (ReferenceEquality<object>.Default);
		Dictionary<MemberCore, MemberLocations> member_locs = new Dictionary<MemberCore, MemberLocations> (ReferenceEquality<MemberCore>.Default);

		[Conditional ("FULL_AST")]
		public void AddLocation (object element, params Location[] locations)
		{
			AddLocation (element, (IEnumerable<Location>)locations);
		}

		[Conditional ("FULL_AST")]
		public void AddLocation (object element, IEnumerable<Location> locations)
		{
			if (element == null || locations == null)
				return;
			simple_locs.Add (element, new List<Location> (locations));
		}

		[Conditional ("FULL_AST")]
		public void AddStatement (object element, params Location[] locations)
		{
			if (element == null)
				return;
			if (locations.Length == 0)
				throw new ArgumentException ("Statement is missing semicolon location");
			simple_locs.Add (element, new List<Location>(locations));
		}

		[Conditional ("FULL_AST")]
		public void AddMember (MemberCore member, IList<Tuple<Modifiers, Location>> modLocations, params Location[] locations)
		{
			LastMember = member;
			if (member == null)
				return;
			
			MemberLocations existing;
			if (member_locs.TryGetValue (member, out existing)) {
				existing.Modifiers = modLocations;
				existing.AddLocations (locations);
				return;
			}
			member_locs.Add (member, new MemberLocations (modLocations, locations));
		}
		[Conditional ("FULL_AST")]
		public void AddMember (MemberCore member, IList<Tuple<Modifiers, Location>> modLocations, IEnumerable<Location> locations)
		{
			LastMember = member;
			if (member == null)
				return;
			
			MemberLocations existing;
			if (member_locs.TryGetValue (member, out existing)) {
				existing.Modifiers = modLocations;
				existing.AddLocations (locations);
				return;
			}
			member_locs.Add (member, new MemberLocations (modLocations, locations));
		}

		[Conditional ("FULL_AST")]
		public void AppendTo (object existing, params Location[] locations)
		{
			AppendTo (existing, (IEnumerable<Location>)locations);

		}

		[Conditional ("FULL_AST")]
		public void AppendTo (object existing, IEnumerable<Location> locations)
		{
			if (existing == null)
				return;
			List<Location> locs;
			if (simple_locs.TryGetValue (existing, out locs)) {
				simple_locs [existing].AddRange (locations);
				return;
			}
			AddLocation (existing, locations);
		}
		
		[Conditional ("FULL_AST")]
		public void AppendToMember (MemberCore existing, params Location[] locations)
		{
			AppendToMember (existing, (IEnumerable<Location>)locations);

		}
		
		[Conditional ("FULL_AST")]
		public void AppendToMember (MemberCore existing, IEnumerable<Location> locations)
		{
			if (existing == null)
				return;
			MemberLocations member;
			if (member_locs.TryGetValue (existing, out member)) {
				member.AddLocations (locations);
				return;
			}
			member_locs.Add (existing, new MemberLocations (null, locations));
		}

		public List<Location> GetLocations (object element)
		{
			if (element == null)
				return null;
			List<Location > found;
			simple_locs.TryGetValue (element, out found);
			return found;
		}

		public MemberLocations GetMemberLocation (MemberCore element)
		{
			MemberLocations found;
			member_locs.TryGetValue (element, out found);
			return found;
		}
	}
	
	public class UsingsBag
	{
		public class Namespace {
			public Location NamespaceLocation { get; set; }
			public MemberName Name { get; set; }
			
			public Location OpenBrace { get; set; }
			public Location CloseBrace { get; set; }
			public Location OptSemicolon { get; set; }
			
			public List<object> usings = new List<object> ();
			public List<object> members = new List<object> ();
			
			public Namespace ()
			{
				// in case of missing close brace, set it to the highest value.
				CloseBrace = new Location (int.MaxValue, int.MaxValue);
			}
			
			public virtual void Accept (StructuralVisitor visitor)
			{
				visitor.Visit (this);
			}
		}
		
		public class AliasUsing
		{
			public readonly Location UsingLocation;
			public readonly Tokenizer.LocatedToken Identifier;
			public readonly Location AssignLocation;
			public readonly ATypeNameExpression Nspace;
			public readonly Location SemicolonLocation;
			
			public AliasUsing (Location usingLocation, Tokenizer.LocatedToken identifier, Location assignLocation, ATypeNameExpression nspace, Location semicolonLocation)
			{
				this.UsingLocation = usingLocation;
				this.Identifier = identifier;
				this.AssignLocation = assignLocation;
				this.Nspace = nspace;
				this.SemicolonLocation = semicolonLocation;
			}
			
			public virtual void Accept (StructuralVisitor visitor)
			{
				visitor.Visit (this);
			}
		}
		
		public class Using
		{
			public readonly Location UsingLocation;
			public readonly ATypeNameExpression NSpace;
			public readonly Location SemicolonLocation;

			public Using (Location usingLocation, ATypeNameExpression nSpace, Location semicolonLocation)
			{
				this.UsingLocation = usingLocation;
				this.NSpace = nSpace;
				this.SemicolonLocation = semicolonLocation;
			}

			public virtual void Accept (StructuralVisitor visitor)
			{
				visitor.Visit (this);
			}
		}
		
		public class ExternAlias 
		{
			public readonly Location ExternLocation;
			public readonly Location AliasLocation;
			public readonly Tokenizer.LocatedToken Identifier;
			public readonly Location SemicolonLocation;
			
			public ExternAlias (Location externLocation, Location aliasLocation, Tokenizer.LocatedToken identifier, Location semicolonLocation)
			{
				this.ExternLocation = externLocation;
				this.AliasLocation = aliasLocation;
				this.Identifier = identifier;
				this.SemicolonLocation = semicolonLocation;
			}

			public virtual void Accept (StructuralVisitor visitor)
			{
				visitor.Visit (this);
			}
		}
		
		public Namespace Global {
			get;
			set;
		}
		Stack<Namespace> curNamespace = new Stack<Namespace> ();
		
		public UsingsBag ()
		{
			Global = new Namespace ();
			Global.OpenBrace = new Location (1, 1);
			Global.CloseBrace = new Location (int.MaxValue, int.MaxValue);
			curNamespace.Push (Global);
		}
		
		[Conditional ("FULL_AST")]
		public void AddUsingAlias (Location usingLocation, Tokenizer.LocatedToken identifier, Location assignLocation, ATypeNameExpression nspace, Location semicolonLocation)
		{
			curNamespace.Peek ().usings.Add (new AliasUsing (usingLocation, identifier, assignLocation, nspace, semicolonLocation));
		}
		
		[Conditional ("FULL_AST")]
		public void AddUsing (Location usingLocation, ATypeNameExpression nspace, Location semicolonLocation)
		{
			curNamespace.Peek ().usings.Add (new Using (usingLocation, nspace, semicolonLocation));
		}

		[Conditional ("FULL_AST")]
		public void AddExternAlias (Location externLocation, Location aliasLocation, Tokenizer.LocatedToken identifier, Location semicolonLocation)
		{
			curNamespace.Peek ().usings.Add (new ExternAlias (externLocation, aliasLocation, identifier, semicolonLocation));
		}
		
		[Conditional ("FULL_AST")]
		public void DeclareNamespace (Location namespaceLocation, MemberName nspace)
		{
			var newNamespace = new Namespace () { NamespaceLocation = namespaceLocation, Name = nspace };
			curNamespace.Peek ().members.Add (newNamespace);
			curNamespace.Push (newNamespace);
		}
		
		int typeLevel = 0;
		[Conditional ("FULL_AST")]
		public void PushTypeDeclaration (object type)
		{
			if (typeLevel == 0)
				curNamespace.Peek ().members.Add (type);
			typeLevel++;
		}
		
		[Conditional ("FULL_AST")]
		public void PopTypeDeclaration ()
		{
			typeLevel--;
		}
		
		[Conditional ("FULL_AST")]
		public void EndNamespace (Location optSemicolon)
		{
			curNamespace.Peek ().OptSemicolon = optSemicolon;
			curNamespace.Pop ();
		}
		
		[Conditional ("FULL_AST")]
		public void EndNamespace ()
		{
			curNamespace.Pop ();
		}
		
		[Conditional ("FULL_AST")]
		public void OpenNamespace (Location bracketLocation)
		{
			curNamespace.Peek ().OpenBrace = bracketLocation;
		}
		
		[Conditional ("FULL_AST")]
		public void CloseNamespace (Location bracketLocation)
		{
			curNamespace.Peek ().CloseBrace = bracketLocation;
		}
	}
}
