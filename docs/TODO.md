* Request actions as a separate request/response
* Fix seed text not working
* Move system prompts into seed text
* Make an IAction that all actions implement - register it keyed
* Tag context in older format for retreival
* No Layers - just tag based. Use FTS5 for searching
* Create MCP server hub, with a "catalog" MCP server exposed to start
* Calleague instead of Participant
* Give context fragments an active weight in the current conversation
* Strip out / display <think> tags



Initial supported context modifications (use JSON patches):
	* Add new fragment (identity creates a proposal fragment that is returned after 10 turns)
	* Fetch existing fragments by tag (possibly other criterea?)
	* Remove fragment (not system or identity)
	* Edit fragment (not system - identity creates a proposal fragment that is returned after 10 turns)
	* Add a tag
	* Swap showing full content / summary for specific fragments

Initial supported actions:
	* Schedule wakeup event
	* Query audit log

Any response with "<continue />" on its own line at the end, will re-send the context (after any edits/actions are performed) before requesting input from the physical colleague


System prompt outline:
	## Introduction
	Hi I'm John, welcome to Persistence, etc.

	## Response Formats
	There are three different response formats:

	### Modifying Context
	Use this when modifying context...
	<context-modification>

	</context-modification>

	### Performing Actions
	Here are the available actions...
	<possible-actions>

	</possible-actions>

	### Displaying Output
	Just use markdown/plain text!

	## Continue Tag
	You can always put <continue /> on a line at the bottom to take another turn!


Working Context Format draft:
	