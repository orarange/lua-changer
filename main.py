import tkinter as tk
from tkinter import filedialog
import os
import re
import time
import shutil
import threading

def copy_xml_to_txt(xml_filepath):
    """XMLファイルの内容をテキストファイルにコピーする。
    コピー先のtxtファイルの名前は選択されたxmlファイルの名前と同じにする。

    Args:
        xml_filepath (str): 入力XMLファイルのパス。
    """
    try:
        with open(xml_filepath, 'r', encoding='utf-8') as xml_file:
            xml_content = xml_file.read()

        # XMLファイル名から拡張子を除いた部分を取得
        txt_filename = os.path.splitext(os.path.basename(xml_filepath))[0] + ".txt"
        # txt_filepathを生成（XMLファイルと同じディレクトリに作成）
        txt_filepath = os.path.join(os.path.dirname(xml_filepath), txt_filename)

        with open(txt_filepath, 'w', encoding='utf-8') as txt_file:
            txt_file.write(xml_content)

        print(f"XMLファイルの内容を{txt_filepath}にコピーしました。")
        return txt_filepath  # txt_filepathを返す

    except FileNotFoundError:
        print("xmlファイルが見つかりませんでした。")
        return None
    except Exception as e:
        print(f"エラーが発生しました: {e}")
        return None

def replace_lua_in_txt(txt_filepath, search_string, replace_lua_filepath, output_xml_filepath, original_xml_filepath):
    """
    テキストファイルから指定された文字列を含むLuaプログラムを見つけ出し、
    指定されたLuaプログラムに置き換え、結果をXMLファイルにコピーする。

    Args:
        txt_filepath (str): 対象のテキストファイルのパス。
        search_string (str): 検索する文字列。
        replace_lua_filepath (str): 置き換えるLuaプログラムのファイルパス。
        output_xml_filepath (str): 出力XMLファイルのパス。
        original_xml_filepath (str): 元のXMLファイルのパス
    """
    try:
        with open(txt_filepath, 'r', encoding='utf-8') as txt_file:
            txt_content = txt_file.read()

        with open(replace_lua_filepath, 'r', encoding='utf-8') as lua_file:
            replace_lua_content = lua_file.read()

        # 検索文字列を含むLuaプログラムを検索
        pattern = re.compile(re.escape(search_string) + r'.*?end', re.DOTALL)
        match = pattern.search(txt_content)

        if match:
            # Luaプログラムを置き換え
            new_txt_content = txt_content.replace(match.group(0), replace_lua_content)

            # テキストファイルに書き込み
            with open(txt_filepath, 'w', encoding='utf-8') as txt_file:
                txt_file.write(new_txt_content)

            print(f"{txt_filepath}内のLuaプログラムを置き換えました。")

            # txtファイルをxmlファイルにコピー
            shutil.copy(txt_filepath, original_xml_filepath)
            print(f"{txt_filepath}を{original_xml_filepath}にコピーしました。")

            # txtファイルを削除
            os.remove(txt_filepath)
            print(f"{txt_filepath}を削除しました。")
        else:
            print(f"{txt_filepath}内に指定されたLuaプログラムが見つかりませんでした。")

    except FileNotFoundError:
        print("ファイルが見つかりませんでした。")
    except Exception as e:
        print(f"エラーが発生しました: {e}")

def browse_file(entry, initialdir=None):
    filename = filedialog.askopenfilename(initialdir=initialdir)
    entry.delete(0, tk.END)
    entry.insert(0, filename)

def run_script():
    """GUIから設定されたパスを使ってスクリプトを実行する。"""
    xml_file_path = xml_file_entry.get()
    txt_file_path = copy_xml_to_txt(xml_file_path)  # copy_xml_to_txtを実行し、txt_filepathを取得
    if txt_file_path is None:
        print("XMLファイルのコピーに失敗しました。")
        return

    search_string = search_string_entry.get()
    replace_lua_file_path = replace_lua_file_entry.get()
    #出力xmlファイルは入力xmlファイルと同じ場所に設定
    output_xml_filepath = os.path.splitext(xml_file_path)[0] + "_new.xml"


    # スクリプトの実行
    time.sleep(2)
    replace_lua_in_txt(txt_file_path, search_string, replace_lua_file_path, output_xml_filepath, xml_file_path)

# GUIの作成
root = tk.Tk()
root.title("Lua Changer")

# デフォルトディレクトリ
default_xml_dir = r"C:\Users\user\AppData\Roaming\Stormworks\data\vehicles"

# ファイルパスのエントリーと参照ボタン
xml_file_label = tk.Label(root, text="入力XMLファイル:")
xml_file_label.grid(row=0, column=0, sticky=tk.W)
xml_file_entry = tk.Entry(root, width=50)
xml_file_entry.grid(row=0, column=1)
xml_file_button = tk.Button(root, text="参照", command=lambda: browse_file(xml_file_entry, default_xml_dir))
xml_file_button.grid(row=0, column=2)

replace_lua_file_label = tk.Label(root, text="置換Luaファイル:")
replace_lua_file_label.grid(row=1, column=0, sticky=tk.W)
replace_lua_file_entry = tk.Entry(root, width=50)
replace_lua_file_entry.grid(row=1, column=1)
replace_lua_file_button = tk.Button(root, text="参照", command=lambda: browse_file(replace_lua_file_entry))
replace_lua_file_button.grid(row=1, column=2)

# 検索文字列のエントリー
search_string_label = tk.Label(root, text="検索文字列:")
search_string_label.grid(row=3, column=0, sticky=tk.W)
search_string_entry = tk.Entry(root, width=50)
search_string_entry.grid(row=3, column=1)
search_string_entry.insert(0, "-- autochanger")  # デフォルト値を設定

# 実行ボタン
run_button = tk.Button(root, text="実行", command=run_script)
run_button.grid(row=4, column=1)

root.mainloop()