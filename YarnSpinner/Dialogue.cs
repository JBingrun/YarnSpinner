/*

The MIT License (MIT)

Copyright (c) 2015 Secret Lab Pty. Ltd. and Yarn Spinner contributors.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

using System;
using System.Collections;
using System.Collections.Generic;

namespace Yarn {

	// Represents things that can go wrong while loading or running
	// a dialogue.
	public  class YarnException : Exception {
		public YarnException(string message) : base(message) {}
	}
		
	// Delegates, which are used by the client.

	// OptionChoosers let the client tell the Dialogue about what
	// response option the user selected.
	public delegate void OptionChooser (int selectedOptionIndex);

	// Loggers let the client send output to a console, for both debugging
	// and error logging.
	public delegate void Logger(string message);

	// Information about stuff that the client should handle.
	// (Currently this just wraps a single field, but doing it like this
	// gives us the option to add more stuff later without breaking the API.)
	public struct Line { public string text; }
	public struct Options { public IList<string> options; }
	public struct Command { public string text; }

	// The Dialogue class is the main thing that clients will use.
	public class Dialogue  {

		// We'll ask this object for the state of variables
		internal VariableStorage continuity;

		// Represents something for the end user ("client") of the Dialogue class to do.
		public abstract class RunnerResult { }

		// The client should run a line of dialogue.
		public class LineResult : RunnerResult  {
			
			public Line line;

			public LineResult (string text) {
				var line = new Line();
				line.text = text;
				this.line = line;
			}

		}

		// The client should run a command (it's up to them to parse the string)
		public class CommandResult: RunnerResult {
			public Command command;

			public CommandResult (string text) {
				var command = new Command();
				command.text = text;
				this.command = command;
			}

		}
			
		// The client should show a list of options, and call 
		// setSelectedOptionDelegate before asking for the 
		// next line. It's an error if you don't.
		public class OptionSetResult : RunnerResult {
			public Options options;
			public OptionChooser setSelectedOptionDelegate;

			public OptionSetResult (IList<string> optionStrings, OptionChooser setSelectedOption) {
				var options = new Options();
				options.options = optionStrings;
				this.options = options;
				this.setSelectedOptionDelegate = setSelectedOption;
			}

		}

		// We've reached the end of this node. Used internally, 
		// and not exposed to clients.
		internal class NodeCompleteResult: RunnerResult {
			public string nextNode;

			public NodeCompleteResult (string nextNode) {
				this.nextNode = nextNode;
			}
		}

		// Delegates used for logging.
		public Logger LogDebugMessage;
		public Logger LogErrorMessage;

		// The node we start from.
		public const string DEFAULT_START = "Start";

		private Loader loader;

		public Library library;

		internal bool stopExecuting = false;

		private HashSet<String> visitedNodeNames = new HashSet<string>();

		public Dialogue(Yarn.VariableStorage continuity) {
			this.continuity = continuity;
			loader = new Loader (this);
			library = new Library ();

			library.ImportLibrary (new StandardLibrary ());

			// Register the "visited" function
			library.RegisterFunction ("visited", 1, delegate(Yarn.Value[] parameters) {
				var name = parameters[0].AsString;
				return visitedNodeNames.Contains(name);
			});

			// Register the "assert" function
			library.RegisterFunction ("assert", 1, delegate(Value[] parameters) {
				if (parameters[0].AsBool == false) {
					stopExecuting = true;
				}
			});
		}

		public int LoadFile(string fileName, bool showTokens = false, bool showParseTree = false, string onlyConsiderNode=null) {
			System.IO.StreamReader reader = new System.IO.StreamReader(fileName);
			string inputString = reader.ReadToEnd ();
			reader.Close ();

			return LoadString (inputString, showTokens, showParseTree, onlyConsiderNode);

		}

		public int LoadString(string text, bool showTokens=false, bool showParseTree=false, string onlyConsiderNode=null) {

			if (LogDebugMessage == null) {
				throw new YarnException ("LogDebugMessage must be set before loading");
			}

			if (LogErrorMessage == null) {
				throw new YarnException ("LogErrorMessage must be set before loading");
			}


			loader.Load(text, library, showTokens, showParseTree, onlyConsiderNode);

			return loader.nodes.Count;
		}

		public IEnumerable<Yarn.Dialogue.RunnerResult> Run(string startNode = DEFAULT_START) {

			stopExecuting = false;

			var runner = new Runner (this);

			if (LogDebugMessage == null) {
				throw new YarnException ("LogDebugMessage must be set before running");
			}

			if (LogErrorMessage == null) {
				throw new YarnException ("LogErrorMessage must be set before running");
			}

			var nextNode = startNode;

			do {

				LogDebugMessage ("Running node " + nextNode);	
				Parser.Node node;

				try {
					node = loader.nodes [nextNode];
				} catch (KeyNotFoundException) {
					LogErrorMessage ("Can't find node " + nextNode);
					yield break;
				}

				foreach (var result in runner.RunNode(node)) {

					// Is it the special command "stop"?
					if (result is CommandResult && (result as CommandResult).command.text == "stop") {
						yield break;
					}

					// Did we get our stop flag set?
					if (stopExecuting) {
						yield break;
					}

					// Are we now done with this node?
					if (result is NodeCompleteResult) {
						var nodeComplete = result as NodeCompleteResult;

						// Move to the next node (or to null)
						nextNode = nodeComplete.nextNode;

						// NodeComplete is not interactive, so skip immediately to next step
						// (which should end this loop)
						continue;
					} 
					yield return result;
				}

				// Register that we've finished with this node. We do this
				// after running the node, not before, so that "visited("Node")" can
				// be called in "Node" itself, and fail. This lets you check to see
				// if, for example, this is the first time you've run this node, without
				// having to add extra variables to keep track of state.
				visitedNodeNames.Add(node.name);

			} while (nextNode != null);

			LogDebugMessage ("Run complete.");

		}

		private class StandardLibrary : Library {

			public StandardLibrary() {

				#region Operators

				this.RegisterFunction(TokenType.Add.ToString(), 2, delegate(Value[] parameters) {

					// If either of these parameters are strings, concatenate them as strings
					if (parameters[0].type == Value.Type.String ||
						parameters[1].type == Value.Type.String) {

						return parameters[0].AsString + parameters[1].AsString;
					}

					// Otherwise, treat them as numbers
					return parameters[0].AsNumber + parameters[1].AsNumber;
				});

				this.RegisterFunction(TokenType.Minus.ToString(), 2, delegate(Value[] parameters) {
					return parameters[0].AsNumber - parameters[1].AsNumber;
				});

				this.RegisterFunction(TokenType.UnaryMinus.ToString(), 1, delegate(Value[] parameters) {
					return -parameters[0].AsNumber;
				});

				this.RegisterFunction(TokenType.Divide.ToString(), 2, delegate(Value[] parameters) {
					return parameters[0].AsNumber / parameters[1].AsNumber;
				});

				this.RegisterFunction(TokenType.Multiply.ToString(), 2, delegate(Value[] parameters) {
					return parameters[0].AsNumber * parameters[1].AsNumber;
				});

				this.RegisterFunction(TokenType.EqualTo.ToString(), 2, delegate(Value[] parameters) {

					// TODO: This may not be the greatest way of doing it

					// Coerce to the type of second operand
					switch (parameters [1].type) {
					case Value.Type.Number:
						return parameters[0].AsNumber == parameters[1].AsNumber;
					case Value.Type.String:
						return parameters[0].AsString == parameters[1].AsString;
					case Value.Type.Bool:
						return parameters[0].AsBool == parameters[1].AsBool;
					case Value.Type.Null:
						// Only null-null comparisons are true.
						return parameters[0].type == Value.Type.Null;
					}

					// Give up and say they're not equal
					return false;

				});

				this.RegisterFunction(TokenType.NotEqualTo.ToString(), 2, delegate(Value[] parameters) {

					// Return the logical negative of the == operator's result
					var equalTo = this.GetFunction(TokenType.EqualTo.ToString());

					return !equalTo.Invoke(parameters).AsBool;
				});

				this.RegisterFunction(TokenType.GreaterThan.ToString(), 2, delegate(Value[] parameters) {
					return parameters[0].AsNumber > parameters[1].AsNumber;
				});

				this.RegisterFunction(TokenType.GreaterThanOrEqualTo.ToString(), 2, delegate(Value[] parameters) {
					return parameters[0].AsNumber >= parameters[1].AsNumber;
				});

				this.RegisterFunction(TokenType.LessThan.ToString(), 2, delegate(Value[] parameters) {
					return parameters[0].AsNumber < parameters[1].AsNumber;
				});

				this.RegisterFunction(TokenType.LessThanOrEqualTo.ToString(), 2, delegate(Value[] parameters) {
					return parameters[0].AsNumber <= parameters[1].AsNumber;
				});

				this.RegisterFunction(TokenType.And.ToString(), 2, delegate(Value[] parameters) {
					return parameters[0].AsBool && parameters[1].AsBool;
				});

				this.RegisterFunction(TokenType.Or.ToString(), 2, delegate(Value[] parameters) {
					return parameters[0].AsBool || parameters[1].AsBool;
				});

				this.RegisterFunction(TokenType.Xor.ToString(), 2, delegate(Value[] parameters) {
					return parameters[0].AsBool ^ parameters[1].AsBool;
				});

				this.RegisterFunction(TokenType.Not.ToString(), 1, delegate(Value[] parameters) {
					return !parameters[0].AsBool;
				});

				#endregion Operators
			}
		}



	}
}