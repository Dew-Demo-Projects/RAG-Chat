import argparse
import psycopg2
from gedcom import parser
from gedcom.element.element import Element
from typing import Dict, Tuple, Optional
import json
import re
import os
from dotenv import load_dotenv


class GedcomToPostgres:
    def __init__(self, gedcom_file: str, db_params: Dict):
        """Initialize the parser with GEDCOM file and database parameters"""
        self.gedcom_file = gedcom_file
        self.db_params = db_params
        self.conn = None
        self.cursor = None
        self.gedcom_parser = None

    def connect_db(self):
        """Establish database connection"""
        self.conn = psycopg2.connect(**self.db_params)
        self.cursor = self.conn.cursor()
        print(f"Connected to database: {self.db_params.get('database')}")

    def disconnect_db(self):
        """Close database connection"""
        if self.cursor:
            self.cursor.close()
        if self.conn:
            self.conn.close()
        print("Database connection closed")

    def parse_gedcom(self):
        """Parse the GEDCOM file using python-gedcom library"""
        self.gedcom_parser = parser.GedcomParser()
        self.gedcom_parser.parse_file(self.gedcom_file)

        # Get root child elements (individuals and families)
        self.root_child_elements = self.gedcom_parser.get_root_child_elements()
        print(f"Parsed GEDCOM file: {self.gedcom_file}")

        # Count individuals and families
        individuals = [e for e in self.root_child_elements if e.get_tag() == 'INDI']
        families = [e for e in self.root_child_elements if e.get_tag() == 'FAM']
        print(f"Individuals found: {len(individuals)}")
        print(f"Families found: {len(families)}")

    def clean_id(self, gedcom_id: str) -> str:
        """Remove @ symbols from GEDCOM IDs"""
        return gedcom_id.strip('@') if gedcom_id else None

    def extract_name_parts(self, name: str) -> Tuple[Optional[str], Optional[str], Optional[str], Optional[str]]:
        """Extract first name, middle names, last name from GEDCOM name format"""
        if not name:
            return None, None, None, None

        # GEDCOM names are often in format: First /Last/ or First Middle /Last/
        display_name = name
        first_name = None
        middle_names = None
        last_name = None

        # Try to parse /Last Name/ format
        match = re.search(r'/([^/]+)/', name)
        if match:
            last_name = match.group(1)
            # Remove the last name from the string for first/middle parsing
            name_without_last = re.sub(r'/[^/]+/', '', name).strip()

            # Split remaining parts
            parts = name_without_last.split()
            if parts:
                first_name = parts[0]
                if len(parts) > 1:
                    middle_names = ' '.join(parts[1:])
        else:
            # No slashes, just use whole name as display
            parts = name.split()
            if parts:
                first_name = parts[0]
                if len(parts) > 1:
                    last_name = parts[-1]
                    if len(parts) > 2:
                        middle_names = ' '.join(parts[1:-1])

        return first_name, middle_names, last_name, display_name

    def get_event_info(self, individual: Element, event_type: str) -> Tuple[Optional[str], Optional[str]]:
        """Extract date and place for birth/death events"""
        date = None
        place = None

        for child in individual.get_child_elements():
            if child.get_tag() == event_type:
                # Look for DATE and PLACE sub-elements
                for sub_child in child.get_child_elements():
                    if sub_child.get_tag() == 'DATE':
                        date = sub_child.get_value()
                    elif sub_child.get_tag() == 'PLACE':
                        place = sub_child.get_value()
                break

        return date, place

    def extract_notes(self, element: Element) -> Optional[str]:
        """Extract notes from GEDCOM element"""
        notes = []

        for child in element.get_child_elements():
            if child.get_tag() == 'NOTE':
                note_text = child.get_value().strip()
                if note_text:
                    notes.append(note_text)

        return '\n'.join(notes) if notes else None

    def get_individual_name(self, individual: Element) -> Optional[str]:
        """Extract name from individual element"""
        for child in individual.get_child_elements():
            if child.get_tag() == 'NAME':
                return child.get_value()
        return None

    def get_individual_gender(self, individual: Element) -> Optional[str]:
        """Extract gender from individual element"""
        for child in individual.get_child_elements():
            if child.get_tag() == 'SEX':
                return child.get_value()
        return None

    def build_extra_json(self, individual: Element) -> Optional[Dict]:
        """Build extra JSON field with additional information"""
        extra = {}

        # Extract alternate names
        alt_names = []
        for child in individual.get_child_elements():
            if child.get_tag() == 'NAME':
                alt_names.append(child.get_value())

        if alt_names and len(alt_names) > 1:
            extra['alternate_names'] = alt_names[1:]  # Skip first name (main name)

        # Extract events with notes
        events = []
        for child in individual.get_child_elements():
            if child.get_tag() in ['BIRT', 'DEAT', 'BAPM', 'BURI', 'CHR', 'DEAT', 'OCCU']:
                event_dict = {'type': child.get_tag()}

                # Extract date and place
                for sub_child in child.get_child_elements():
                    if sub_child.get_tag() == 'DATE':
                        event_dict['date'] = sub_child.get_value()
                    elif sub_child.get_tag() == 'PLACE':
                        event_dict['place'] = sub_child.get_value()
                    elif sub_child.get_tag() == 'NOTE':
                        event_notes = self.extract_notes(child)
                        if event_notes:
                            event_dict['notes'] = event_notes

                events.append(event_dict)

        if events:
            extra['events'] = events

        # Add other custom tags
        custom_tags = {}
        for child in individual.get_child_elements():
            if child.get_tag().startswith('_'):
                custom_tags[child.get_tag()] = child.get_value()

        if custom_tags:
            extra['custom_tags'] = custom_tags

        return extra if extra else None

    def process_individuals(self):
        """Process and insert individual records"""
        print("\nProcessing individuals...")

        individuals = [e for e in self.root_child_elements if e.get_tag() == 'INDI']
        processed_count = 0

        for individual in individuals:
            # Extract GEDCOM ID
            gedcom_id = self.clean_id(individual.get_pointer())

            # Extract name information
            name = self.get_individual_name(individual)
            first_name, middle_names, last_name, display_name = self.extract_name_parts(name)

            # Extract gender
            gender = self.get_individual_gender(individual)

            # Extract birth and death information
            birth_date, birth_place = self.get_event_info(individual, 'BIRT')
            death_date, death_place = self.get_event_info(individual, 'DEAT')

            # Determine living status
            is_living = None
            if death_date or death_place:
                is_living = False
            # Note: We can't reliably determine if someone is living without death info

            # Extract notes
            notes = self.extract_notes(individual)

            # Build extra JSON
            extra = self.build_extra_json(individual)

            # Insert into database
            try:
                self.cursor.execute("""
                    INSERT INTO persons 
                    (id, first_name, middle_names, last_name, display_name, gender, 
                     birth_date_raw, birth_place, death_date_raw, death_place, 
                     is_living, notes, extra)
                    VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
                    ON CONFLICT (id) DO UPDATE SET
                        first_name = EXCLUDED.first_name,
                        middle_names = EXCLUDED.middle_names,
                        last_name = EXCLUDED.last_name,
                        display_name = EXCLUDED.display_name,
                        gender = EXCLUDED.gender,
                        birth_date_raw = EXCLUDED.birth_date_raw,
                        birth_place = EXCLUDED.birth_place,
                        death_date_raw = EXCLUDED.death_date_raw,
                        death_place = EXCLUDED.death_place,
                        is_living = EXCLUDED.is_living,
                        notes = EXCLUDED.notes,
                        extra = EXCLUDED.extra
                """, (
                    gedcom_id, first_name, middle_names, last_name, display_name, gender,
                    birth_date, birth_place, death_date, death_place,
                    is_living, notes, json.dumps(extra) if extra else None
                ))

                processed_count += 1

            except Exception as e:
                print(f"Error inserting individual {gedcom_id}: {e}")
                self.conn.rollback()
                raise

        self.conn.commit()
        print(f"Inserted/updated {processed_count} individuals")

    def get_family_member(self, family: Element, role: str) -> Optional[str]:
        """Get husband or wife ID from family"""
        for child in family.get_child_elements():
            if child.get_tag() == role:
                return self.clean_id(child.get_value())
        return None

    def get_family_marriage_info(self, family: Element) -> Tuple[Optional[str], Optional[str]]:
        """Extract marriage date and place from family"""
        date = None
        place = None

        for child in family.get_child_elements():
            if child.get_tag() == 'MARR':
                for sub_child in child.get_child_elements():
                    if sub_child.get_tag() == 'DATE':
                        date = sub_child.get_value()
                    elif sub_child.get_tag() == 'PLACE':
                        place = sub_child.get_value()
                break

        return date, place

    def get_family_children(self, family: Element) -> list:
        """Get list of children IDs from family"""
        children = []
        for child in family.get_child_elements():
            if child.get_tag() == 'CHIL':
                children.append(self.clean_id(child.get_value()))
        return children

    def process_families(self):
        """Process and insert family records"""
        print("\nProcessing families...")

        families = [e for e in self.root_child_elements if e.get_tag() == 'FAM']
        processed_count = 0

        for family in families:
            # Extract GEDCOM ID
            family_id = self.clean_id(family.get_pointer())

            # Extract husband and wife IDs
            husband_id = self.get_family_member(family, 'HUSB')
            wife_id = self.get_family_member(family, 'WIFE')

            # Extract marriage information
            marriage_date, marriage_place = self.get_family_marriage_info(family)

            # Extract notes
            notes = self.extract_notes(family)

            # Build extra JSON for family
            extra = {}
            family_events = []

            for child in family.get_child_elements():
                if child.get_tag() not in ['HUSB', 'WIFE', 'CHIL', 'MARR']:
                    event_dict = {'type': child.get_tag()}

                    for sub_child in child.get_child_elements():
                        if sub_child.get_tag() == 'DATE':
                            event_dict['date'] = sub_child.get_value()
                        elif sub_child.get_tag() == 'PLACE':
                            event_dict['place'] = sub_child.get_value()

                    event_notes = self.extract_notes(child)
                    if event_notes:
                        event_dict['notes'] = event_notes

                    family_events.append(event_dict)

            if family_events:
                extra['events'] = family_events

            # Insert family record
            try:
                self.cursor.execute("""
                    INSERT INTO families 
                    (id, husband_id, wife_id, marriage_date_raw, marriage_place, notes, extra)
                    VALUES (%s, %s, %s, %s, %s, %s, %s)
                    ON CONFLICT (id) DO UPDATE SET
                        husband_id = EXCLUDED.husband_id,
                        wife_id = EXCLUDED.wife_id,
                        marriage_date_raw = EXCLUDED.marriage_date_raw,
                        marriage_place = EXCLUDED.marriage_place,
                        notes = EXCLUDED.notes,
                        extra = EXCLUDED.extra
                """, (
                    family_id, husband_id, wife_id,
                    marriage_date, marriage_place,
                    notes, json.dumps(extra) if extra else None
                ))

                # Process children for this family
                children = self.get_family_children(family)
                for child_id in children:
                    if child_id:  # Only process if we have a child ID
                        try:
                            self.cursor.execute("""
                                INSERT INTO family_children (family_id, child_id)
                                VALUES (%s, %s)
                                ON CONFLICT (family_id, child_id) DO NOTHING
                            """, (family_id, child_id))
                        except Exception as e:
                            print(f"Error inserting child {child_id} to family {family_id}: {e}")
                            self.conn.rollback()
                            raise

                processed_count += 1
                print(f"  Family {family_id}: {len(children)} children")

            except Exception as e:
                print(f"Error inserting family {family_id}: {e}")
                self.conn.rollback()
                raise

        self.conn.commit()
        print(f"Inserted/updated {processed_count} families")

    def create_rag_chunks(self):
        """Create basic RAG chunks from parsed data"""
        print("\nCreating basic RAG chunks...")

        # Get source type IDs
        self.cursor.execute("SELECT id, code FROM rag_source_types")
        source_types = {row[1]: row[0] for row in self.cursor.fetchall()}

        # Create person chunks
        self.cursor.execute("""
            SELECT id, first_name, last_name, display_name, 
                   birth_date_raw, death_date_raw, gender, notes
            FROM persons
        """)

        persons = self.cursor.fetchall()
        for person in persons:
            person_id = person[0]
            # Create a natural language description
            description_parts = []

            if person[3]:  # display_name
                description_parts.append(f"{person[3]}")
            elif person[1] or person[2]:  # first_name or last_name
                name = f"{person[1] or ''} {person[2] or ''}".strip()
                description_parts.append(name)

            if person[4]:  # birth date
                description_parts.append(f"born {person[4]}")
            if person[5]:  # death date
                description_parts.append(f"died {person[5]}")
            if person[6]:  # gender
                description_parts.append(f"gender: {person[6]}")
            if person[7]:  # notes
                description_parts.append(f"Notes: {person[7]}")

            content = ". ".join(description_parts) + "."

            try:
                self.cursor.execute("""
                    INSERT INTO rag_chunks (source_type_id, source_id, content, metadata)
                    VALUES (%s, %s, %s, %s)
                    ON CONFLICT DO NOTHING
                """, (
                    source_types['person'],
                    f"@{person_id}@",
                    content,
                    json.dumps({"person_id": f"@{person_id}@", "scope": "bio"})
                ))
            except Exception as e:
                print(f"Error creating RAG chunk for person {person_id}: {e}")
                self.conn.rollback()
                raise

        # Create family chunks
        self.cursor.execute("""
            SELECT f.id, p1.display_name, p2.display_name, f.marriage_date_raw
            FROM families f
            LEFT JOIN persons p1 ON f.husband_id = p1.id
            LEFT JOIN persons p2 ON f.wife_id = p2.id
        """)

        families = self.cursor.fetchall()
        for family in families:
            family_id = family[0]
            husband_name = family[1]
            wife_name = family[2]
            marriage_date = family[3]

            description_parts = ["Family"]
            if husband_name and wife_name:
                description_parts.append(f"Husband: {husband_name}, Wife: {wife_name}")
            elif husband_name:
                description_parts.append(f"Husband: {husband_name}")
            elif wife_name:
                description_parts.append(f"Wife: {wife_name}")

            if marriage_date:
                description_parts.append(f"Married: {marriage_date}")

            content = ". ".join(description_parts) + "."

            try:
                self.cursor.execute("""
                    INSERT INTO rag_chunks (source_type_id, source_id, content, metadata)
                    VALUES (%s, %s, %s, %s)
                    ON CONFLICT DO NOTHING
                """, (
                    source_types['family'],
                    f"@{family_id}@",
                    content,
                    json.dumps({"family_id": f"@{family_id}@"})
                ))
            except Exception as e:
                print(f"Error creating RAG chunk for family {family_id}: {e}")
                self.conn.rollback()
                raise

        self.conn.commit()
        print(f"Created RAG chunks for {len(persons)} persons and {len(families)} families")

    def run(self):
        """Main execution method"""
        try:
            self.connect_db()
            self.parse_gedcom()
            self.process_individuals()
            self.process_families()
            self.create_rag_chunks()
            print("\nImport completed successfully!")

        except Exception as e:
            print(f"Error during import: {e}")
            raise

        finally:
            self.disconnect_db()


def load_db_config():
    """Load database configuration from environment variables or .env file"""
    # Try to load from .env file first
    load_dotenv()

    # Get configuration from environment variables
    config = {
        'host': os.getenv('PGHOST'),
        'port': os.getenv('PGPORT'),
        'database': os.getenv('PGDATABASE'),
        'user': os.getenv('PGUSER'),
        'password': os.getenv('PGPASSWORD')
    }

    # Check if all required configuration is present
    missing = [key for key, value in config.items() if not value]
    if missing:
        raise ValueError(f"Missing database configuration: {', '.join(missing)}. "
                         f"Please set these in your .env file or environment variables.")

    return config


def main():
    parser = argparse.ArgumentParser(description='Import GEDCOM file to PostgreSQL database')
    parser.add_argument('gedcom_file', help='Path to GEDCOM file')
    parser.add_argument('--env-file', default='.env', help='Path to .env file (default: .env)')

    args = parser.parse_args()

    # Check if GEDCOM file exists
    if not os.path.exists(args.gedcom_file):
        print(f"Error: GEDCOM file '{args.gedcom_file}' not found.")
        return

    # Check if .env file exists (warn if not, but still try environment variables)
    if not os.path.exists(args.env_file):
        print(f"Warning: .env file '{args.env_file}' not found. Using environment variables.")

    try:
        # Load database configuration
        db_config = load_db_config()
        print(f"Loaded database configuration for: {db_config['database']}@{db_config['host']}")

        # Create and run importer
        importer = GedcomToPostgres(args.gedcom_file, db_config)
        importer.run()

    except ValueError as e:
        print(f"Configuration error: {e}")
        print("\nPlease create a .env file with the following variables:")
        print("PGHOST=your_host")
        print("PGPORT=5432")
        print("PGDATABASE=your_database")
        print("PGUSER=your_user")
        print("PGPASSWORD=your_password")
        return
    except Exception as e:
        print(f"Error during import: {e}")
        return


if __name__ == '__main__':
    main()