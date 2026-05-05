"""PyInstaller entry point — uses absolute imports to avoid relative import errors."""
from orchestrator.__main__ import main

if __name__ == "__main__":
    main()
