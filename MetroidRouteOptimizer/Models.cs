namespace MetroidRouteOptimizer
{
    public class RawGameState
    {
        public List<string> ItemTypes { get; set; } = new();
        public List<Item> Items { get; set; } = new();
        public List<Screen> Screens { get; set; } = new();
        public int StartingScreen { get; set; }
        public int ExitScreen { get; set; }

        public GameState ToGameState()
        {
            return new GameState
            {
                ItemTypeList = ItemTypes,
                ItemList = Items,
                ScreenList = Screens,
                StartingScreen = StartingScreen,
                ExitScreen = ExitScreen
            };
        }
    }

    public class GameState
    {
        // Populated from RawGameState
        public List<string> ItemTypeList { get; set; } = new();
        public List<Item> ItemList { get; set; } = new();
        public List<Screen> ScreenList { get; set; } = new();
        public int StartingScreen { get; set; }
        public int ExitScreen { get; set; }

        // Lookup dictionaries
        public Dictionary<int, ItemType> ItemTypes { get; private set; } = new();
        public Dictionary<int, Item> Items { get; private set; } = new();
        public Dictionary<int, Screen> Screens { get; private set; } = new();

        public void BuildDictionaries()
        {
            // Build item types
            ItemTypes = ItemTypeList
                .Select((name, index) => new ItemType
                {
                    Id = index,
                    Name = name
                })
                .ToDictionary(t => t.Id);

            // Build items
            Items = ItemList.ToDictionary(i => i.Id);

            // Build screens
            Screens = ScreenList.ToDictionary(s => s.Id);
        }

        public void ValidateInitialState()
        {
            // Validate items
            foreach (var item in ItemList)
            {
                if (!ItemTypes.ContainsKey(item.Type))
                {
                    throw new InvalidOperationException(
                        $"Item with ID {item.Id} has an invalid type index: {item.Type}");
                }

                // Check each requirement type index
                foreach (var andGroup in item.Requirements)
                {
                    foreach (var typeIndex in andGroup)
                    {
                        if (!ItemTypes.ContainsKey(typeIndex))
                        {
                            throw new InvalidOperationException(
                                $"Item with ID {item.Id} has a requirement referencing invalid type index: {typeIndex}");
                        }
                    }
                }
            }

            // Validate screens and exits
            foreach (var screen in ScreenList)
            {
                foreach (var exit in screen.Exits)
                {
                    // Check exit requirements
                    foreach (var andGroup in exit.Requirements)
                    {
                        foreach (var typeIndex in andGroup)
                        {
                            if (!ItemTypes.ContainsKey(typeIndex))
                            {
                                throw new InvalidOperationException(
                                    $"Exit from screen {screen.Id} to {exit.DestinationScreenId} " +
                                    $"has invalid type index: {typeIndex}");
                            }
                        }
                    }

                    // Check if exit destination is valid
                    if (!Screens.ContainsKey(exit.DestinationScreenId))
                    {
                        throw new InvalidOperationException(
                            $"Exit from screen {screen.Id} leads to invalid screen ID {exit.DestinationScreenId}");
                    }
                }
            }
        }

        /// <summary>
        /// True if at least one of the AND-groups is fully satisfied by the type counts.
        /// For example, [ [0,1], [2] ] => (Missile AND Bomb) OR (EnergyTank).
        /// </summary>
        public bool AreRequirementsMet(List<List<int>> requirements, Dictionary<int, int> typeCounts)
        {
            // If there's no requirement group, it's trivially met
            if (requirements.Count == 0) return true;

            foreach (var andGroup in requirements)
            {
                bool groupSatisfied = true;
                foreach (var requiredType in andGroup)
                {
                    // Need at least 1 of this requiredType
                    if (!typeCounts.TryGetValue(requiredType, out var cnt) || cnt < 1)
                    {
                        groupSatisfied = false;
                        break;
                    }
                }
                if (groupSatisfied) return true;
            }

            return false;
        }
    }

    // Basic model classes
    public class ItemType
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class Item
    {
        public int Id { get; set; }
        public int Type { get; set; }
        public List<List<int>> Requirements { get; set; } = new();
        public int Weight { get; set; } = 1;
    }

    public class Exit
    {
        public int DestinationScreenId { get; set; }
        public List<List<int>> Requirements { get; set; } = new();
        public int Weight { get; set; } = 1;
    }

    public class Screen
    {
        public int Id { get; set; }
        public List<int> Items { get; set; } = new();
        public List<Exit> Exits { get; set; } = new();
    }

    public class State
    {
        public int CurrentScreenId { get; set; }

        // Which specific item IDs we've collected
        public HashSet<int> CollectedItemIds { get; set; } = new();

        // Count of each item type we hold. Key=type, Value=count
        public Dictionary<int, int> CollectedTypeCounts { get; set; } = new();

        public int Weight { get; set; }
        public List<string> Steps { get; set; } = new();
    }

    // Simple PriorityQueue for Dijkstra-like usage
    public class PriorityQueue<TElement, TPriority>
    {
        private readonly SortedDictionary<TPriority, Queue<TElement>> _dictionary = new();
        public int Count { get; private set; }

        public void Enqueue(TElement element, TPriority priority)
        {
            if (!_dictionary.ContainsKey(priority))
            {
                _dictionary[priority] = new Queue<TElement>();
            }
            _dictionary[priority].Enqueue(element);
            Count++;
        }

        public TElement Dequeue()
        {
            if (_dictionary.Count == 0)
            {
                throw new InvalidOperationException("The queue is empty.");
            }

            var firstPair = _dictionary.First();
            var element = firstPair.Value.Dequeue();

            if (firstPair.Value.Count == 0)
            {
                _dictionary.Remove(firstPair.Key);
            }
            Count--;
            return element;
        }
    }
}
