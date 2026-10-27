#!/usr/bin/env python3
from publish_dev import main, Blocked
import sys

if __name__ == "__main__":
    try:
        main()
    except (Blocked, KeyboardInterrupt) as error:
        print("publish-dev blocked: " + str(error), file=sys.stderr)
        sys.exit(1)
