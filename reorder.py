import csv

# Modify these to your liking
input_order = ["S", "A3", "A2", "A1", "A0", "B3", "B2", "B1", "B0"]
input_file = "table.tsv"
output_order = ["Y(3:0)"]
output_file = "table_reordered.tsv"

reordered_columns = ["#"] + input_order + output_order

input_order_index = []
output_order_index = []

unordered_rows = []
unordered_headers = []

with open(input_file, 'r') as in_file:
    reader = csv.reader(in_file, delimiter="\t")
    unordered_headers = next(reader, None)
    for row in reader:
        unordered_rows.append(row)

for sorted_input in input_order:
    # Find the position of the sorted_input header in the unordered headers
    for i, unordered_header in enumerate(unordered_headers):
        if unordered_header == sorted_input:
            input_order_index.append(i)
            break

for sorted_output in output_order:
    # Find the position of the sorted_input header in the unordered headers
    for i, unordered_header in enumerate(unordered_headers):
        if unordered_header == sorted_output:
            output_order_index.append(i)
            break        

def generateSortKey(row, index_order):
    key = ""
    for i in index_order:
        key += row[i]
    return key

ordered_rows = sorted(unordered_rows, key=lambda x: generateSortKey(x, input_order_index))

if len(input_order) != len(input_order_index):
    print(unordered_headers)
    print(input_order)
    print(input_order_index)
    print("asset len(input_order) == len(input_order_index)")
    exit(1)
    
with open(output_file, 'w') as out_file:
    writer = csv.writer(out_file, dialect="excel-tab")
    writer.writerow(reordered_columns)
    for i, row in enumerate(ordered_rows):
        r = [i]
        for i in input_order_index:
            r.append(row[i])
        for i in output_order_index:
            r.append(row[i])
        writer.writerow(r)

print("Saved to: ", output_file)
