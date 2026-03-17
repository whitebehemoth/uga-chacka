import json
import sys
import os

def check_dictionary(file_path):
    if not os.path.exists(file_path):
        print(f"Error: File {file_path} not found.")
        return

    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except Exception as e:
        print(f"Error reading JSON: {e}")
        return

    errors_found = False
    
    for word, entries in data.items():
        refs = []
        for i, entry in enumerate(entries):
            ref = entry.get('ref')
            
            # 1. Check if 'ref' exists
            if ref is None:
                print(f"Error in word '{word}': Entry at index {i} is missing 'ref'")
                errors_found = True
            else:
                # 2. Check if 'ref' is unique within the word
                if ref in refs:
                    print(f"Error in word '{word}': 'ref' '{ref}' is not unique (found at index {i})")
                    errors_found = True
                refs.append(ref)

    if not errors_found:
        print("No issues found in the dictionary.")

if __name__ == "__main__":
    target_file = 'dic.json'
    if len(sys.argv) > 1:
        target_file = sys.argv[1]
    
    check_dictionary(target_file)
