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

            // Find the optimal route
            var (fullRoute, bestPartial) = FindOptimalRoute(gameState, rawGameState.ExitScreen);

            // Output the result
            Console.WriteLine("\nOptimal Route:");
            if (fullRoute.Count == 0)
            {
                Console.WriteLine("(No valid full route found.)");

                if (bestPartial.Count > 0)
                {
                    Console.WriteLine("\nBest Partial Route (collected the most items):");
                    foreach (var step in bestPartial)
                    {
                        Console.WriteLine(step);
                    }
                }
                else
                {
                    Console.WriteLine("(No meaningful partial route either.)");
                }
            }
            else
            {
                foreach (var step in fullRoute)
                {
                    Console.WriteLine(step);
                }
            }
        }

        /// <summary>
        /// Finds the route that collects all items and ends on exitScreen.
        /// If no full route is found, returns the best partial route as well.
        /// </summary>
        static (List<string> fullRoute, List<string> bestPartial) FindOptimalRoute(GameState gameState, int exitScreen)
        {
            var fullRouteSteps = new List<string>();
            var priorityQueue = new PriorityQueue<State, int>();

            // Visited states: (screenId, sortedCollectedItemIds)
            var visitedStates = new HashSet<(int, string)>();

            // For diagnostics
            var itemsEverCollected = new HashSet<int>();
            var screensEverVisited = new HashSet<int>();

            // Track best partial route
            State bestPartialState = null;
            int maxItemsCollected = 0;
            int bestPartialWeight = int.MaxValue;

            // Initialize starting state
            var startState = new State
            {
                CurrentScreenId = gameState.StartingScreen,
                CollectedItemIds = new HashSet<int>(),
                CollectedTypeCounts = new Dictionary<int, int>(),
                Weight = 0,
                Steps = new List<string>()
            };

            // Collect items in the starting room
            var collectedNow = CollectItemsInRoom(startState, gameState, itemsEverCollected);
            UpdateBestPartialIfNeeded(startState, ref bestPartialState, ref maxItemsCollected, ref bestPartialWeight);
            priorityQueue.Enqueue(startState, 0);

            while (priorityQueue.Count > 0)
            {
                var state = priorityQueue.Dequeue();

                // Mark that we visited this screen (for diagnostics)
                screensEverVisited.Add(state.CurrentScreenId);

                // Create the visited key
                var sortedIds = string.Join(",", state.CollectedItemIds.OrderBy(x => x));
                var stateKey = (state.CurrentScreenId, sortedIds);

                // *** Do NOT mark visited until after we try re-collecting items on THIS state. ***
                // This ensures we don't skip re-checking the same screen with newly collected items.

                // Possibly collect more items in the same screen
                // but do so on a *new* cloned state so we can re-enqueue if we get new items.
                var sameScreenState = CloneState(state);
                var reCollected = CollectItemsInRoom(sameScreenState, gameState, itemsEverCollected);

                if (reCollected)
                {
                    // We found new items in the same room
                    UpdateBestPartialIfNeeded(sameScreenState, ref bestPartialState, ref maxItemsCollected, ref bestPartialWeight);

                    var sameScreenSortedIds = string.Join(",", sameScreenState.CollectedItemIds.OrderBy(x => x));
                    var sameScreenKey = (sameScreenState.CurrentScreenId, sameScreenSortedIds);
                    if (!visitedStates.Contains(sameScreenKey))
                    {
                        priorityQueue.Enqueue(sameScreenState, sameScreenState.Weight);
                    }
                }

                // Now we consider moving from the *original* state to other screens
                // (Alternatively, we could move from sameScreenState, but let's keep consistent.)
                var currentScreen = gameState.Screens[state.CurrentScreenId];
                foreach (var exit in currentScreen.Exits)
                {
                    if (gameState.AreRequirementsMet(exit.Requirements, state.CollectedTypeCounts))
                    {
                        var usedItems = GetSatisfiedRequirements(exit.Requirements, state.CollectedTypeCounts, gameState);

                        // Build a move message
                        var moveMessage = $"Move from screen {state.CurrentScreenId} to screen {exit.DestinationScreenId}";
                        if (exit.Requirements.Count > 0 && usedItems != "(none)")
                        {
                            moveMessage += $" using {usedItems}";
                        }

                        var newState = CloneState(state);
                        newState.CurrentScreenId = exit.DestinationScreenId;
                        newState.Weight = state.Weight + exit.Weight;
                        newState.Steps.Add(moveMessage);

                        var nextCollected = CollectItemsInRoom(newState, gameState, itemsEverCollected);
                        UpdateBestPartialIfNeeded(newState, ref bestPartialState, ref maxItemsCollected, ref bestPartialWeight);

                        var newSortedIds = string.Join(",", newState.CollectedItemIds.OrderBy(x => x));
                        var newStateKey = (newState.CurrentScreenId, newSortedIds);

                        if (!visitedStates.Contains(newStateKey))
                        {
                            priorityQueue.Enqueue(newState, newState.Weight);
                        }
                    }
                }

                // *** Now that we've tried re-collecting items AND tried exits, we can mark visited ***
                if (!visitedStates.Contains(stateKey))
                {
                    visitedStates.Add(stateKey);
                }

                // Check if it was a full route
                if (state.CollectedItemIds.Count == gameState.Items.Count &&
                    state.CurrentScreenId == exitScreen)
                {
                    fullRouteSteps = state.Steps;
                    return (fullRouteSteps, new List<string>());
                }
            }

            // No full route found, diagnose
            DiagnoseNoRoute(gameState, exitScreen, itemsEverCollected, screensEverVisited);

            var bestPartialSteps = bestPartialState?.Steps ?? new List<string>();
            return (new List<string>(), bestPartialSteps);
        }

        /// <summary>
        /// Attempts to collect items in the current room if requirements are met.
        /// Returns true if new items were collected.
        /// </summary>
        static bool CollectItemsInRoom(State state, GameState gameState, HashSet<int> itemsEverCollected)
        {
            var currentScreen = gameState.Screens[state.CurrentScreenId];
            var collectedNow = false;

            foreach (var itemId in currentScreen.Items.ToList())
            {
                var item = gameState.Items[itemId];
                if (gameState.AreRequirementsMet(item.Requirements, state.CollectedTypeCounts))
                {
                    state.CollectedItemIds.Add(itemId);
                    itemsEverCollected.Add(itemId);

                    if (!state.CollectedTypeCounts.ContainsKey(item.Type))
                        state.CollectedTypeCounts[item.Type] = 0;
                    state.CollectedTypeCounts[item.Type]++;

                    currentScreen.Items.Remove(itemId);

                    var itemTypeName = gameState.ItemTypes[item.Type].Name;
                    state.Steps.Add($"Collect {itemTypeName} [ID={itemId}] on screen {state.CurrentScreenId}");

                    collectedNow = true;
                }
            }
            return collectedNow;
        }

        /// <summary>
        /// Diagnoses why no route was found, logging which items/screens weren't reached
        /// </summary>
        static void DiagnoseNoRoute
        (
            GameState gameState,
            int exitScreen,
            HashSet<int> itemsEverCollected,
            HashSet<int> screensEverVisited
        )
        {
            Console.WriteLine("\n--- No route found. Diagnostics:");

            // 1. Which items were never collected
            var allItemIds = gameState.Items.Keys;
            var neverCollected = allItemIds.Where(id => !itemsEverCollected.Contains(id)).ToList();
            if (neverCollected.Count > 0)
            {
                Console.WriteLine("  The following items were never collectible:");
                foreach (var itemId in neverCollected)
                {
                    var item = gameState.Items[itemId];
                    Console.WriteLine($"    - Item ID={itemId} (Type={item.Type} - {gameState.ItemTypes[item.Type].Name})");
                }
            }
            else
            {
                Console.WriteLine("  All items were collected at least once by some partial route, but no route satisfied all conditions.");
            }

            // 2. Check if exit screen was visited
            if (!screensEverVisited.Contains(exitScreen))
            {
                Console.WriteLine($"  The exit screen ({exitScreen}) was never visited in any route.");
            }
            else
            {
                Console.WriteLine($"  The exit screen ({exitScreen}) was visited, but never with all items collected.");
            }
        }

        /// <summary>
        /// If this state is better than the current best partial (more items or less weight), update it.
        /// </summary>
        static void UpdateBestPartialIfNeeded
        (
            State currentState,
            ref State bestPartialState,
            ref int maxItemsCollected,
            ref int bestPartialWeight
        )
        {
            int itemCount = currentState.CollectedItemIds.Count;
            if (itemCount > maxItemsCollected)
            {
                bestPartialState = CloneState(currentState);
                maxItemsCollected = itemCount;
                bestPartialWeight = currentState.Weight;
            }
            else if (itemCount == maxItemsCollected && currentState.Weight < bestPartialWeight)
            {
                bestPartialState = CloneState(currentState);
                bestPartialWeight = currentState.Weight;
            }
        }

        /// <summary>
        /// Clones the state, so we don't keep referencing the same objects.
        /// </summary>
        static State CloneState(State state)
        {
            return new State
            {
                CurrentScreenId = state.CurrentScreenId,
                CollectedItemIds = new HashSet<int>(state.CollectedItemIds),
                CollectedTypeCounts = new Dictionary<int, int>(state.CollectedTypeCounts),
                Weight = state.Weight,
                Steps = new List<string>(state.Steps)
            };
        }

        /// <summary>
        /// Identifies which AND-group in the requirement is satisfied and returns a string 
        /// like "Missile AND Bomb". If none is needed, returns "(none)".
        /// </summary>
        static string GetSatisfiedRequirements
        (
            List<List<int>> requirementGroups,
            Dictionary<int, int> typeCounts,
            GameState gameState
        )
        {
            if (requirementGroups.Count == 0)
            {
                return "(none)";
            }

            foreach (var andGroup in requirementGroups)
            {
                bool groupSatisfied = true;
                foreach (var requiredType in andGroup)
                {
                    if (!typeCounts.TryGetValue(requiredType, out var cnt) || cnt < 1)
                    {
                        groupSatisfied = false;
                        break;
                    }
                }
                if (groupSatisfied)
                {
                    var typeNames = andGroup
                        .Select(t => gameState.ItemTypes[t].Name)
                        .ToArray();

                    if (typeNames.Length == 0)
                    {
                        return "(none)";
                    }
                    return string.Join(" AND ", typeNames);
                }
            }
            return "(unspecified)";
        }
    }
}
