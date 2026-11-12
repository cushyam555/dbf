# Mixer Studio

[![Build Windows EXE](https://github.com/cushyam555/dbf/actions/workflows/windows.yml/badge.svg)](https://github.com/cushyam555/dbf/actions/workflows/windows.yml)

โปรแกรม Windows ภาษาไทยสำหรับสแกน ตรวจไฟล์ประกอบ และคัดลอกคลังเพลง EMK / NCN โดยคงโครงสร้างโฟลเดอร์เดิม

## ดาวน์โหลดโปรแกรม Windows

1. เปิด [Build Windows EXE](https://github.com/cushyam555/dbf/actions/workflows/windows.yml)
2. เลือกงานล่าสุดที่แสดงเครื่องหมายถูกสีเขียว
3. เลื่อนลงไปที่ **Artifacts** แล้วดาวน์โหลด **MixerStudio-Windows-x64** (ต้องเข้าสู่ระบบ GitHub)
4. แตก ZIP แล้วเปิด **MixerStudio.exe** บน Windows 10/11 แบบ 64 บิต ไม่ต้องติดตั้ง .NET เพิ่ม

ไฟล์ดาวน์โหลดของแต่ละงานเก็บไว้ 30 วัน เจ้าของ repository สามารถกด **Run workflow** เพื่อสร้างไฟล์ใหม่ได้

## สิ่งที่ทำได้ในรุ่น 0.1

- สแกนไฟล์ EMK และ MIDI ในโฟลเดอร์ย่อย
- ตรวจไฟล์ประกอบ NCN (.lyr / .cur) จากชื่อไฟล์ พร้อมแจ้งไฟล์ขาดหรือซ้ำ
- ค้นหา เลือกเพลง และคัดลอกโดยไม่แก้ไขต้นฉบับ
- ยกเลิกงานและบันทึกรายงานผลการคัดลอก

**ยังไม่อ่าน DBF และยังไม่แปลง EMK ↔ NCN** รุ่นนี้ยังไม่รองรับทุกฟังก์ชันของโปรแกรมต้นแบบ และยังต้องทดสอบกับคลังเพลงจริง

## การสร้างและตรวจสอบ

ใช้ .NET 8 SDK บน Windows แล้วรัน `./build.ps1` ใน PowerShell จะได้ไฟล์ที่ `dist/MixerStudio.exe`

GitHub Actions รันชุดตรวจสอบส่วนประมวลผล สร้าง EXE พร้อม runtime และตรวจว่าโปรแกรมเปิดหน้าต่างและปิดได้บน Windows การตรวจนี้ยังไม่ครอบคลุมการกดใช้งานทุกส่วนของ UI

ดูวิธีใช้และข้อจำกัดโดยละเอียดใน [คู่มือภาษาไทย](README-TH.md)
