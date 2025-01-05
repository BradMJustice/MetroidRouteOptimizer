using System.Text.Json;

namespace MetroidRouteOptimizer
{
    class Program
    {
        static void Main(string[] args)
        {
            // Determine the project directory (go up two levels from the base directory)
            var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            var jsonFilePath = Path.Combine(projectDirectory, "data.json");

            if (!File.Exists(jsonFilePath))
            {
                Console.WriteLine($"JSON file not found at: {jsonFilePath}");
                return;
            }

            // Configure JSON serializer options for camelCase
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            // Load and parse game state from JSON file
            var json = File.ReadAllText(jsonFilePath);
            var rawGameState = JsonSerializer.Deserialize<RawGameState>(json, jsonOptions);

            if (rawGameState == null)
            {
                Console.WriteLine("Failed to parse the JSON data.");
                return;
            }

            // Convert raw game state and build dictionaries
            var gameState = rawGameState.ToGameState();
            gameState.BuildDictionaries();

            // Validate the initial state
            try
            {
                gameState.ValidateInitialState();
                Console.WriteLine("Initial state validation passed.");
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"Validation error: {ex.Message}");
                return;
            }

            // Single-pass Dijkstra
            var (fullRoute, totalCost, bestPartialRoute, bestPartialItems) =
                FindRouteCollectAllAndReachExit(gameState);

            if (fullRoute.Count == 0)
            {
                // No full route found
                Console.WriteLine("\nNo valid route found that collects all items and reaches the exit.");

                if (bestPartialRoute.Count > 0)
                {
                    Console.WriteLine($"\nBest Partial Route (collected {bestPartialItems} of {gameState.Items.Count} items):");
                    foreach (var step in bestPartialRoute)
                    {
                        Console.WriteLine(step);
                    }
                }

                // Optional diagnostics
                DiagnoseNoRoute(gameState, bestPartialItems);
            }
            else
            {
                Console.WriteLine($"\nFound a valid route! Total cost = {totalCost}.\n");
                foreach (var step in fullRoute)
                {
                    Console.WriteLine(step);
                }
            }
        }

        /// <summary>
        /// Runs a single-pass Dijkstra on (screenId + collectedItemIds).
        /// Costs:
        ///  - +1 per exit traversal
        ///  - +1 per item pickup
        /// Returns:
        ///  - A full route if we manage to collect all items and reach the exit
        ///  - Otherwise, the best partial route (most items, tie broken by lowest cost)
        /// </summary>
        static
        (
            List<string> fullRoute,
            int totalCost,
            List<string> bestPartialRoute,
            int bestPartialItems
        )
        FindRouteCollectAllAndReachExit(GameState gameState)
        {
            var pq = new PriorityQueue<State, int>();

            // (screenId, sortedCollectedItems) -> best known cost
            var visitedStates = new Dictionary<(int, string), int>();

            // Track best partial
            State bestPartialState = null;
            int maxItemsCollected = 0;
            int bestPartialWeight = int.MaxValue;

            // Create the start state
            var startState = new State
            {
                CurrentScreenId = gameState.StartingScreen,
                CollectedItemIds = new HashSet<int>(),
                CollectedTypeCounts = new Dictionary<int, int>(),
                Weight = 0,
                Steps = new List<string>()
            };

            // Collect any items in the starting screen
            TryCollectAllItemsInScreen(startState, gameState);
            UpdateBestPartialIfNeeded(startState, ref bestPartialState, ref maxItemsCollected, ref bestPartialWeight);

            pq.Enqueue(startState, startState.Weight);

            while (pq.Count > 0)
            {
                var current = pq.Dequeue();

                // Check for victory
                if (current.CollectedItemIds.Count == gameState.Items.Count &&
                    current.CurrentScreenId == gameState.ExitScreen)
                {
                    // Found a route with all items
                    return (current.Steps, current.Weight, new List<string>(), gameState.Items.Count);
                }

                // Build visited key
                var sortedItems = string.Join(",", current.CollectedItemIds.OrderBy(x => x));
                var key = (current.CurrentScreenId, sortedItems);

                // Skip if we've seen it cheaper
                if (visitedStates.TryGetValue(key, out var oldCost))
                {
                    if (current.Weight >= oldCost)
                    {
                        continue;
                    }
                }
                visitedStates[key] = current.Weight;

                // Update best partial
                UpdateBestPartialIfNeeded(current, ref bestPartialState, ref maxItemsCollected, ref bestPartialWeight);

                // Try each exit from the current screen
                var screen = gameState.Screens[current.CurrentScreenId];
                foreach (var exit in screen.Exits)
                {
                    // If we meet item requirements for the exit
                    if (gameState.AreRequirementsMet(exit.Requirements, current.CollectedTypeCounts))
                    {
                        var newState = CloneState(current);
                        newState.CurrentScreenId = exit.DestinationScreenId;
                        newState.Weight += 1; // cost to traverse exit

                        // Log
                        var reqDesc = GetSatisfiedRequirements(exit.Requirements, current.CollectedTypeCounts, gameState);
                        if (reqDesc == "(none)")
                        {
                            newState.Steps.Add($"Move from screen {current.CurrentScreenId} to screen {exit.DestinationScreenId} (cost +1)");
                        }
                        else
                        {
                            newState.Steps.Add($"Move from screen {current.CurrentScreenId} to screen {exit.DestinationScreenId} using {reqDesc} (cost +1)");
                        }

                        // Attempt to collect items in the new screen
                        TryCollectAllItemsInScreen(newState, gameState);

                        // Enqueue
                        pq.Enqueue(newState, newState.Weight);
                    }
                }
            }

            // No route found that collects all items & reaches exit.
            var partialSteps = bestPartialState?.Steps ?? new List<string>();
            var partialItemCount = bestPartialState?.CollectedItemIds.Count ?? 0;
            return (new List<string>(), 0, partialSteps, partialItemCount);
        }

        /// <summary>
        /// In the given state's current screen, repeatedly pick up items that we haven't
        /// collected yet, if we meet their requirements. Each pickup costs +1.
        /// 
        /// IMPORTANT: We do NOT remove items from the global screen.Items.
        /// We rely on 'state.CollectedItemIds' to see if we've already picked an item up.
        /// </summary>
        static bool TryCollectAllItemsInScreen(State state, GameState gameState)
        {
            bool collectedAnything = false;
            var screen = gameState.Screens[state.CurrentScreenId];

            bool itemCollected;
            do
            {
                itemCollected = false;
                // We iterate over all items in this screen, but do NOT remove them from 'screen.Items'
                foreach (var itemId in screen.Items)
                {
                    // If we haven't already collected this item
                    if (!state.CollectedItemIds.Contains(itemId))
                    {
                        var item = gameState.Items[itemId];
                        // Check if we can pick it up
                        if (gameState.AreRequirementsMet(item.Requirements, state.CollectedTypeCounts))
                        {
                            // +1 cost
                            state.Weight += 1;

                            // Mark as collected
                            state.CollectedItemIds.Add(itemId);
                            if (!state.CollectedTypeCounts.ContainsKey(item.Type))
                            {
                                state.CollectedTypeCounts[item.Type] = 0;
                            }
                            state.CollectedTypeCounts[item.Type]++;

                            // Log
                            var itemName = gameState.ItemTypes[item.Type].Name;
                            var reqDesc = GetSatisfiedRequirements(item.Requirements, state.CollectedTypeCounts, gameState);
                            if (reqDesc == "(none)")
                            {
                                state.Steps.Add($"Collect item {itemId} ({itemName}) on screen {state.CurrentScreenId} (cost +1)");
                            }
                            else
                            {
                                state.Steps.Add($"Collect item {itemId} ({itemName}) on screen {state.CurrentScreenId} using {reqDesc} (cost +1)");
                            }

                            collectedAnything = true;
                            itemCollected = true;
                            // Break to re-check the same screen in case we unlocked new items
                            break;
                        }
                    }
                }
            }
            while (itemCollected);

            return collectedAnything;
        }

        /// <summary>
        /// If 'current' is a better partial (i.e. more items, or same items but less weight),
        /// update bestPartialState.
        /// </summary>
        static void UpdateBestPartialIfNeeded
        (
            State current,
            ref State bestPartialState,
            ref int maxItemsCollected,
            ref int bestPartialWeight
        )
        {
            var itemCount = current.CollectedItemIds.Count;
            if (itemCount > maxItemsCollected)
            {
                bestPartialState = CloneState(current);
                maxItemsCollected = itemCount;
                bestPartialWeight = current.Weight;
            }
            else if (itemCount == maxItemsCollected && current.Weight < bestPartialWeight)
            {
                bestPartialState = CloneState(current);
                bestPartialWeight = current.Weight;
            }
        }

        /// <summary>
        /// If no full route was found, log some basic diagnostics.
        /// </summary>
        static void DiagnoseNoRoute(GameState gameState, int bestPartialItemsCollected)
        {
            if (bestPartialItemsCollected == gameState.Items.Count)
            {
                // So we have all items, but never reached the exit
                Console.WriteLine("\nDiagnostic: Collected all items, but couldn't reach the exit.");
            }
            else
            {
                var uncollected = gameState.Items.Count - bestPartialItemsCollected;
                Console.WriteLine($"\nDiagnostic: {uncollected} item(s) remain uncollected. Possibly unreachable due to item requirements.");
            }
        }

        /// <summary>
        /// Builds a string describing which AND-group of the requirement is satisfied,
        /// e.g. "Missile AND Bomb", or "(none)" if no requirement.
        /// </summary>
        static string GetSatisfiedRequirements
        (
            List<List<int>> requirementGroups,
            Dictionary<int, int> typeCounts,
            GameState gameState
        )
        {
            if (requirementGroups.Count == 0) return "(none)";

            foreach (var andGroup in requirementGroups)
            {
                bool groupSatisfied = true;
                foreach (var requiredType in andGroup)
                {
                    if (!typeCounts.TryGetValue(requiredType, out var c) || c < 1)
                    {
                        groupSatisfied = false;
                        break;
                    }
                }
                if (groupSatisfied)
                {
                    var names = andGroup.Select(t => gameState.ItemTypes[t].Name).ToArray();
                    if (names.Length == 0) return "(none)";
                    return string.Join(" AND ", names);
                }
            }
            return "(unspecified)";
        }

        /// <summary>
        /// Clones the given state so we can modify it without affecting the original.
        /// </summary>
        static State CloneState(State original)
        {
            return new State
            {
                CurrentScreenId = original.CurrentScreenId,
                CollectedItemIds = new HashSet<int>(original.CollectedItemIds),
                CollectedTypeCounts = new Dictionary<int, int>(original.CollectedTypeCounts),
                Weight = original.Weight,
                Steps = new List<string>(original.Steps)
            };
        }
    }
}
